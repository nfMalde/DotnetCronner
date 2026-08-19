using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetCronner;

/// <summary>
/// The background scheduler. On start it discovers attribute tasks and seeds all registered tasks into
/// the store, then repeatedly claims due tasks (highest priority first) and dispatches them to a pool
/// of workers bounded by <see cref="CronnerOptions.MaxConcurrentTasks"/>.
/// </summary>
public sealed class CronnerHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly CronnerStoreAccessor _stores;
    private readonly CronnerRegistry _registry;
    private readonly CronnerScheduleCalculator _calculator;
    private readonly CronnerScheduleSignal _signal;
    private readonly CronnerExecutionTracker _tracker;
    private readonly CronnerHookDispatcher _hooks;
    private readonly CronnerJobServices _jobServices;
    private readonly CronnerOptions _options;
    private readonly ILogger<CronnerHostedService> _logger;
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private IServiceProvider _jobProvider;

    // Claimed-or-running jobs owned by this instance. The poll loop only claims what the workers can pick up
    // immediately (degree - _inFlight): a claimed job that waits in the queue has no heartbeat, so if it waited
    // longer than LockTtl its lock would lapse and it could be claimed (and run) a second time.
    private int _inFlight;
    private int _capacityStarved;

    /// <summary>The claim token of this scheduler instance (recorded as <see cref="CronnerJobExecution.Owner"/>).</summary>
    public string Owner => _owner;

    /// <summary>Creates the hosted service.</summary>
    public CronnerHostedService(
        IServiceProvider services,
        CronnerStoreAccessor stores,
        CronnerRegistry registry,
        CronnerScheduleCalculator calculator,
        CronnerScheduleSignal signal,
        CronnerExecutionTracker tracker,
        CronnerHookDispatcher hooks,
        CronnerJobServices jobServices,
        IOptions<CronnerOptions> options,
        ILogger<CronnerHostedService> logger)
    {
        _services = services;
        _stores = stores;
        _registry = registry;
        _calculator = calculator;
        _signal = signal;
        _tracker = tracker;
        _hooks = hooks;
        _jobServices = jobServices;
        _options = options.Value;
        _logger = logger;
        _jobProvider = services;
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Discover and seed tasks before the background loop starts so they are immediately queryable
        // (and controllable) via ICronnerClient once the host has started.
        RegisterAttributeTasks();

        // Resolve the provider tasks run against — the app provider by default, or a dedicated one.
        var jobTypes = _registry.Descriptors.Where(d => !d.Method.IsStatic).Select(d => d.TargetType);
        _jobProvider = _jobServices.Resolve(_services, jobTypes);

        WarnAboutLockSettings();

        await SeedAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    // The lease-safety rule (see HeartbeatLoopAsync) relies on renewals landing well inside the TTL: a renewal
    // that fails is retried while the last confirmed expiry is still ahead, so the cadence decides how many
    // retries fit before the run must be abandoned. Say so loudly when the configuration leaves no slack.
    private void WarnAboutLockSettings()
    {
        var interval = _options.EffectiveKeepAliveInterval();
        if (interval >= _options.LockTtl)
        {
            _logger.LogError(
                "DotnetCronner KeepAliveInterval ({KeepAlive}) is not below LockTtl ({LockTtl}): a running task's lock can lapse between renewals and be reclaimed. Set KeepAliveInterval well below LockTtl (LockTtl/2 is the default).",
                interval, _options.LockTtl);
        }
        else if (interval > _options.LockTtl / 2)
        {
            _logger.LogWarning(
                "DotnetCronner KeepAliveInterval ({KeepAlive}) is above LockTtl/2 ({Half}): a single failed renewal may force a run to be abandoned because no retry fits before the lease lapses. LockTtl/2 or less is recommended.",
                interval, _options.LockTtl / 2);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var degree = Math.Max(1, _options.MaxConcurrentTasks);
        var channel = Channel.CreateBounded<CronnerJob>(new BoundedChannelOptions(degree)
        {
            SingleReader = false,
            SingleWriter = true,
        });

        var workers = new Task[degree];
        for (var i = 0; i < degree; i++)
            workers[i] = Task.Run(() => WorkerLoopAsync(channel.Reader, stoppingToken), CancellationToken.None);

        try
        {
            await PollLoopAsync(channel.Writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private void RegisterAttributeTasks()
    {
        var assemblies = new List<Assembly>();
        if (_options.ScanEntryAssembly && Assembly.GetEntryAssembly() is { } entry)
            assemblies.Add(entry);
        assemblies.AddRange(_options.AdditionalAssemblies);

        if (assemblies.Count == 0)
            return;

        foreach (var descriptor in AttributeScanner.Scan(assemblies, _options.DiscoveryTypeFilters))
            _registry.Add(descriptor);
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var neverFiring = new List<string>();
        foreach (var descriptor in _registry.Descriptors)
        {
            try
            {
                var existing = await _stores.UseAsync(store => store.GetByIdAsync(descriptor.Id, cancellationToken)).ConfigureAwait(false);
                if (existing is null)
                {
                    var next = _calculator.GetNextOccurrence(descriptor.CronString, now, _options.TimeZone);
                    var neverFires = NeverFires(descriptor, next);
                    if (neverFires)
                        neverFiring.Add(descriptor.Id);
                    await _stores.UseAsync(store => store.UpsertAsync(new CronnerJob { Id = descriptor.Id, Name = descriptor.Name, CronExpression = descriptor.CronString, Priority = descriptor.Priority, NextRunUtc = next, State = neverFires ? CronnerTaskState.Failed : next is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Idle, LastError = neverFires ? NeverFiresMessage : null, }, cancellationToken)).ConfigureAwait(false);
                }
                else
                {
                    // Refresh metadata from code; keep the persisted schedule/state across restarts.
                    existing.Name = descriptor.Name;
                    existing.CronExpression = descriptor.CronString;
                    existing.Priority = descriptor.Priority;
                    if (existing.NextRunUtc is null && descriptor.CronString is not null &&
                        existing.State is CronnerTaskState.Idle or CronnerTaskState.Scheduled)
                    {
                        existing.NextRunUtc = _calculator.GetNextOccurrence(descriptor.CronString, now, _options.TimeZone);
                        if (NeverFires(descriptor, existing.NextRunUtc))
                        {
                            existing.State = CronnerTaskState.Failed;
                            existing.LastError = NeverFiresMessage;
                            neverFiring.Add(descriptor.Id);
                        }
                        else if (existing.NextRunUtc is not null)
                        {
                            existing.State = CronnerTaskState.Scheduled;
                        }
                    }

                    await _stores.UseAsync(store => store.UpsertAsync(existing, cancellationToken)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed DotnetCronner task {TaskId}.", descriptor.Id);
            }
        }

        // Fail-fast option: refuse to start when any task can never fire (see CronnerOptions.OnInvalidSchedule).
        if (_options.OnInvalidSchedule == CronnerInvalidScheduleBehavior.Throw && neverFiring.Count > 0)
            throw new InvalidOperationException(
                $"DotnetCronner: {neverFiring.Count} task(s) have a cron expression that parses but never fires: " +
                $"{string.Join(", ", neverFiring)}. Fix the expression(s), or set " +
                "CronnerOptions.OnInvalidSchedule = MarkFailed to mark them Failed and start anyway.");
    }

    private const string NeverFiresMessage = "Cron expression parses but never produces a next occurrence.";

    // A cron that parses cleanly but has no next occurrence (e.g. 31 February) is a silent killer: the task
    // just never runs. Treat it loudly (log + mark Failed). Seeding is per-task and wrapped in try/catch, so
    // one such task can never prevent the others from being scheduled.
    private bool NeverFires(CronnerJobDescriptor descriptor, DateTimeOffset? next)
    {
        if (descriptor.CronString is null || next is not null)
            return false;

        _logger.LogError(
            "DotnetCronner task {TaskId} has a cron expression that never fires ('{Cron}') — it will not run and is marked Failed. Fix the expression.",
            descriptor.Id, descriptor.CronString);
        return true;
    }

    private async Task PollLoopAsync(ChannelWriter<CronnerJob> writer, CancellationToken stoppingToken)
    {
        var degree = Math.Max(1, _options.MaxConcurrentTasks);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Claim only what a free worker can start right away. A claim is a lease with a TTL; the
                // heartbeat that keeps it alive only starts once a worker picks the job up, so a job that sat
                // in the queue for longer than LockTtl would become claimable again while still queued here —
                // the one way a single instance could run the same task twice.
                var free = degree - Volatile.Read(ref _inFlight);
                if (free <= 0)
                {
                    Volatile.Write(ref _capacityStarved, 1);
                }
                else
                {
                    var due = await _stores
                        .UseAsync(store => store.AcquireDueAsync(
                            DateTimeOffset.UtcNow, _owner, _options.LockTtl, free, stoppingToken))
                        .ConfigureAwait(false);

                    Interlocked.Add(ref _inFlight, due.Count);
                    foreach (var job in due)
                        await writer.WriteAsync(job, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DotnetCronner poll loop failed; retrying next tick.");
            }

            try
            {
                await _signal.WaitAsync(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task WorkerLoopAsync(ChannelReader<CronnerJob> reader, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A worker must never die: log and take the next job.
                    _logger.LogError(ex, "DotnetCronner worker failed while processing task {TaskId}.", job.Id);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    // The poll loop skipped a tick because every worker was busy; wake it now that one is free.
                    if (Interlocked.Exchange(ref _capacityStarved, 0) == 1)
                        _signal.Signal();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task ProcessAsync(CronnerJob job, CancellationToken stoppingToken)
    {
        // Bracket the whole run so the store can open a per-run session before any store action and always
        // close it at the very end, even if the run throws.
        await SafeStoreStartAsync(job, stoppingToken).ConfigureAwait(false);
        try
        {
            await ProcessCoreAsync(job, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            await SafeStoreCloseAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessCoreAsync(CronnerJob job, CancellationToken stoppingToken)
    {
        // One-off instances carry a unique Id but run the method of their definition; recurring jobs use Id.
        _registry.TryGet(job.DefinitionId ?? job.Id, out var descriptor);
        var concurrent = descriptor is not null && descriptor.Concurrency == CronnerConcurrencyMode.Concurrent;

        // One execution id and one state bag for the whole run, shared by the job and every hook of the run.
        // The id exists whether or not history is persisted, so consumers can always key their own records to it.
        var runState = new CronnerRunState(Guid.NewGuid().ToString("N"));

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // A non-concurrent task never runs twice at once in this process: if a claim for a task that is already
        // running here ever arrived (it cannot after capacity-aware claiming, but this is the guarantee people
        // rely on), refuse it rather than start a second copy. Concurrent-mode runs may overlap by design.
        if (concurrent)
        {
            _tracker.Register(job.Id, jobCts);
        }
        else if (!_tracker.TryRegister(job.Id, jobCts))
        {
            _logger.LogError(
                "DotnetCronner claimed task {TaskId} while a run of it is still in progress on this instance; the claim is ignored to prevent a concurrent execution.",
                job.Id);
            return;
        }

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.FromResult(HeartbeatOutcome.Completed);
        try
        {
            // Every lock-holding mode gets a heartbeat that renews the claim while the task runs; it starts
            // BEFORE any user hook so a slow OnLockAcquire cannot eat into the lease. Its first act is to confirm
            // the claim is still ours (the job may have waited a moment between claim and pickup): a definitive
            // "no" means another owner holds it and we must not run. Concurrent mode releases the lock up front
            // and needs no keepalive.
            if (descriptor is not null && !concurrent)
            {
                var confirmedUntil = await ConfirmClaimAsync(job, stoppingToken).ConfigureAwait(false);
                if (confirmedUntil is null)
                {
                    _logger.LogWarning(
                        "DotnetCronner task {TaskId} is no longer claimed by this instance (it was reclaimed, released or cancelled before the run could start); skipping it.",
                        job.Id);
                    return;
                }

                heartbeat = HeartbeatLoopAsync(descriptor, job, jobCts, heartbeatCts.Token, runState, confirmedUntil.Value);
            }

            // The claim happened in the poll loop; announce it as the worker takes the job.
            await FireLockHookAsync(CronnerHookEvent.LockAcquire, descriptor, job, stoppingToken, runState).ConfigureAwait(false);

            if (descriptor is null)
            {
                _logger.LogWarning("No descriptor registered for task {TaskId}; unscheduling it.", job.Id);
                job.State = CronnerTaskState.Failed;
                job.LastError = "No descriptor registered for this task.";
                job.NextRunUtc = null;
                ReleaseLock(job);
                await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
                await SafeReleaseLockAsync(job.Id, stoppingToken).ConfigureAwait(false);
                await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken, runState).ConfigureAwait(false);
                return;
            }

            var dueTime = job.NextRunUtc ?? DateTimeOffset.UtcNow;

            // In concurrent mode, advance the schedule up front so the next occurrence can run in parallel.
            if (concurrent)
            {
                job.NextRunUtc = _calculator.GetNextOccurrence(descriptor.CronString, DateTimeOffset.UtcNow, _options.TimeZone);
                job.State = job.NextRunUtc is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Running;
                ReleaseLock(job);
                await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
                await SafeReleaseLockAsync(job.Id, stoppingToken).ConfigureAwait(false);
                await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken, runState).ConfigureAwait(false);
            }
            else
            {
                job.State = CronnerTaskState.Running;
                job.LastRunUtc = DateTimeOffset.UtcNow;
                await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
            }

            // Execution history (opt-in via WithExecutionHistory): insert a Running record now and finalize it
            // after the run. The per-run id lets the store finalize the exact row even when a Concurrent-mode
            // task has several runs of the same job id in flight at once.
            CronnerJobExecution? execution = null;
            if (_options.ExecutionHistoryRetentionCount > 0)
            {
                var now = DateTimeOffset.UtcNow;
                execution = new CronnerJobExecution
                {
                    Id = runState.ExecutionId,
                    JobId = job.Id,
                    StartedAt = now,
                    Status = JobExecutionStatus.Running,
                    Attempt = job.RetryCount + 1,
                    Owner = _owner,   // which scheduler instance ran this
                };
                runState.Execution = execution;

                // We hold this task's lock, so any older record still marked Running belongs to a run whose
                // owner died or lost its lease without finalizing — close it before recording ours. (Concurrent
                // mode legitimately overlaps, so it is left alone.)
                if (!concurrent)
                    await SafeFinalizeOrphanedExecutionsAsync(job.Id, now, stoppingToken).ConfigureAwait(false);

                await SafeRecordExecutionStartedAsync(execution, stoppingToken).ConfigureAwait(false);
            }

            bool cancelledByClient;
            Exception? failure;
            HeartbeatOutcome outcome;
            try
            {
                (cancelledByClient, failure) = await RunWithHooksAsync(descriptor, job, jobCts, runState, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                heartbeatCts.Cancel();
                outcome = await heartbeat.ConfigureAwait(false);
            }

            var lockLost = outcome == HeartbeatOutcome.LockLost;

            // Finalize the history record (terminal status + finish time + error + any consumer data), then trim.
            // A lock-lost run finalizes its OWN row (keyed by its execution id) so it does not linger as Running.
            if (execution is not null)
                await FinalizeExecutionAsync(execution, cancelledByClient, failure, lockLost, runState, stoppingToken).ConfigureAwait(false);

            if (concurrent)
            {
                await FinalizeConcurrentAsync(job.Id, failure, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (lockLost)
            {
                // The claim lapsed (or could no longer be confirmed) mid-run; another worker may own the task
                // now. Writing our (stale) outcome would clobber the reclaiming worker's state, so we stop here.
                _logger.LogWarning(
                    "DotnetCronner task {TaskId} lost its execution lock during the run and was abandoned; the reclaiming worker owns it now.",
                    job.Id);
                await FireLockHookAsync(CronnerHookEvent.LockLost, descriptor, job, stoppingToken, runState).ConfigureAwait(false);
                // If the claim is in fact still ours (the lease was merely unconfirmable), free it now so the task
                // is re-run without waiting for the lease to lapse; if it was reclaimed, this is a no-op.
                await SafeReleaseLockAsync(job.Id, stoppingToken).ConfigureAwait(false);
                return;
            }

            job.RunCount++;
            ReleaseLock(job);
            ApplyOutcome(job, descriptor, cancelledByClient, failure, dueTime);

            // A cancel from another instance (ICronnerClient.CancelTaskAsync elsewhere) lands in the store as
            // State = Cancelled while we run; honour it instead of re-arming the schedule over it.
            if (!cancelledByClient && await IsCancelledInStoreAsync(job.Id, stoppingToken).ConfigureAwait(false))
            {
                cancelledByClient = true;
                job.State = CronnerTaskState.Cancelled;
                job.NextRunUtc = null;
            }

            // A manual trigger that arrived mid-run takes precedence and runs immediately.
            if (!cancelledByClient && _tracker.ConsumePendingTrigger(job.Id))
            {
                job.NextRunUtc = DateTimeOffset.UtcNow;
                job.State = CronnerTaskState.Scheduled;
            }

            // Persist the outcome first, release the lock second: the task must not be claimable while its
            // schedule still points at the occurrence we just ran.
            await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
            await SafeReleaseLockAsync(job.Id, stoppingToken).ConfigureAwait(false);
            await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken, runState).ConfigureAwait(false);

            // The run is over: stop tracking it, then honour a trigger that arrived while we were finalizing
            // (it was parked as pending because the task still counted as running).
            _tracker.Unregister(job.Id);
            if (!cancelledByClient && _tracker.ConsumePendingTrigger(job.Id))
            {
                job.NextRunUtc = DateTimeOffset.UtcNow;
                job.State = CronnerTaskState.Scheduled;
                await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
            }

            // Built-in retention: keep only the newest N finished one-off instances of this definition.
            if (job.Kind == CronnerJobKind.OneOff && job.DefinitionId is not null &&
                _options.OneOffRetentionCount > 0 &&
                job.State is CronnerTaskState.Completed or CronnerTaskState.Failed)
            {
                await SafePruneAsync(job.DefinitionId, _options.OneOffRetentionCount, stoppingToken).ConfigureAwait(false);
            }

            if (job.NextRunUtc is { } next && next <= DateTimeOffset.UtcNow)
                _signal.Signal();
        }
        finally
        {
            heartbeatCts.Cancel();
            if (!heartbeat.IsCompleted)
                await heartbeat.ConfigureAwait(false);
            _tracker.Unregister(job.Id);
        }
    }

    /// <summary>
    /// Confirms, right before a run starts, that this instance still holds the task's claim, by renewing it. Returns
    /// the confirmed expiry, or <c>null</c> when the store says the claim is no longer ours. When the store cannot
    /// answer (it threw), the claim's own expiry is used as long as it is still ahead — the lease is ours until then,
    /// and the heartbeat keeps trying to confirm it.
    /// </summary>
    private async Task<DateTimeOffset?> ConfirmClaimAsync(CronnerJob job, CancellationToken stoppingToken)
    {
        var until = DateTimeOffset.UtcNow + _options.LockTtl;
        try
        {
            var renewed = await _stores
                .UseAsync(store => store.RenewLockAsync(job.Id, _owner, until, stoppingToken))
                .ConfigureAwait(false);
            return renewed ? until : null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner could not confirm the claim for task {TaskId} before the run; continuing on the claim's own expiry.", job.Id);
            return job.LockedUntilUtc is { } claimed && claimed > DateTimeOffset.UtcNow ? claimed : null;
        }
    }

    private async Task<bool> IsCancelledInStoreAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _stores.UseAsync(store => store.GetByIdAsync(id, cancellationToken)).ConfigureAwait(false);
            return latest?.State == CronnerTaskState.Cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DotnetCronner could not re-read task {TaskId} after its run.", id);
            return false;
        }
    }

    private async Task FinalizeExecutionAsync(
        CronnerJobExecution execution, bool cancelled, Exception? failure, bool lockLost, CronnerRunState runState,
        CancellationToken stoppingToken)
    {
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.Status = lockLost || cancelled ? JobExecutionStatus.Cancelled
            : failure is not null ? JobExecutionStatus.Failed : JobExecutionStatus.Succeeded;
        execution.Error = lockLost ? CronnerExecutionErrors.LockLost : failure?.Message;
        execution.Data = SerializeExecutionData(runState.ExecutionData);
        await SafeRecordExecutionFinishedAsync(execution, stoppingToken).ConfigureAwait(false);
        await SafePruneExecutionsAsync(execution.JobId, _options.ExecutionHistoryRetentionCount, stoppingToken).ConfigureAwait(false);
    }

    private Task FireLockHookAsync(
        CronnerHookEvent hookEvent, CronnerJobDescriptor? descriptor, CronnerJob job,
        CancellationToken cancellationToken, CronnerRunState? runState = null) =>
        _hooks.DispatchAsync(hookEvent, _jobProvider, descriptor, job, TimeSpan.Zero, null, cancellationToken, runState: runState);

    private async Task<(bool Cancelled, Exception? Failure)> RunWithHooksAsync(
        CronnerJobDescriptor descriptor, CronnerJob job, CancellationTokenSource jobCts, CronnerRunState runState,
        CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var cancelled = false;
        Exception? failure = null;

        // The terminal lifecycle hooks (Start/Success/Fail/Cancel) run in the SAME scope as the task, so a
        // hook's ctx.HasParam<T>() resolves the very scoped instances the job used — e.g. a hook reads back
        // a summary the job wrote into a scoped service. (Lock/progress hooks keep their own scope.)
        await using (var scope = _jobProvider.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;

            // Wire the progress context the task resolves from this same scope; reports reach the hooks and,
            // for total progress, are persisted via a targeted update that never touches the lock. The same
            // scoped context also carries the run-state bag, so the job can ctx.Set(...) for its hooks.
            var progress = sp.GetService<CronnerJobContext>();
            progress?.AttachRunState(runState);
            progress?.Initialize((hookEvent, total, scopeInfo, payload) =>
            {
                if (hookEvent == CronnerHookEvent.TotalProgressChange)
                {
                    job.Progress = total;
                    _ = SafeUpdateProgressAsync(job.Id, total, stoppingToken);
                }

                return _hooks.DispatchAsync(
                    hookEvent, _jobProvider, descriptor, job, TimeSpan.Zero, null, stoppingToken, total, scopeInfo, runState,
                    progressPayload: payload);
            });

            await DispatchLifecycleAsync(CronnerHookEvent.Start, sp, descriptor, job, TimeSpan.Zero, null, runState, false, stoppingToken)
                .ConfigureAwait(false);

            try
            {
                await CronnerJobInvoker.InvokeAsync(sp, descriptor, jobCts.Token, job.Payload, job.PayloadType).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.LogError(ex, "DotnetCronner task {TaskId} failed.", job.Id);
            }

            // Let any fire-and-forget progress hooks finish before the terminal hook runs.
            if (progress is not null)
                await progress.DrainAsync().ConfigureAwait(false);

            var terminal = cancelled
                ? CronnerHookEvent.Cancel
                : failure is not null ? CronnerHookEvent.Fail : CronnerHookEvent.Success;
            // On failure, tell the Fail hook whether a retry is still coming (so it can hold off on alerting).
            var willRetry = terminal == CronnerHookEvent.Fail && job.RetryCount < _options.DefaultMaxRetries;
            await DispatchLifecycleAsync(terminal, sp, descriptor, job, stopwatch.Elapsed, failure, runState, willRetry, stoppingToken)
                .ConfigureAwait(false);
        }

        return (cancelled, failure);
    }

    // Terminal lifecycle hooks (Start/Success/Fail/Cancel) are routed per hook: each uses its own scope
    // preference (set via AddHook/WithHook) or, when it set none, CronnerOptions.HookScope — Shared runs it in
    // the job's execution scope (so ctx.HasParam<T>() resolves the job's scoped instances), Isolated gives it
    // its own fresh scope. Either way the run-state bag is threaded through.
    private Task DispatchLifecycleAsync(
        CronnerHookEvent hookEvent, IServiceProvider jobScope, CronnerJobDescriptor descriptor, CronnerJob job,
        TimeSpan duration, Exception? exception, CronnerRunState runState, bool willRetry, CancellationToken cancellationToken) =>
        _hooks.DispatchTerminalAsync(
            hookEvent, jobScope, _jobProvider, descriptor, job, duration, exception, cancellationToken,
            _options.HookScope, runState, willRetry);

    private static string? SerializeExecutionData(object? data) =>
        data is null ? null : CronnerPayloadSerializer.Serialize(data, data.GetType());

    /// <summary>How the heartbeat of a run ended.</summary>
    private enum HeartbeatOutcome
    {
        /// <summary>The run finished (or the host stopped) while the lock was held.</summary>
        Completed,

        /// <summary>The lock was lost or could no longer be confirmed before it lapsed; the run was cancelled.</summary>
        LockLost,

        /// <summary>The task was set to <see cref="CronnerTaskState.Cancelled"/> in the store (a cancel from another instance); the run was cancelled.</summary>
        Cancelled,
    }

    /// <summary>
    /// Keeps the execution lock alive while a task runs, renewing about halfway through the TTL so a slow store call
    /// or a GC pause has slack before the lock lapses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The lease rule.</b> A renewal that the store answers <c>false</c> is a definitive loss (the claim was
    /// reclaimed, released, or the task cancelled) and the run is cancelled at once. A renewal the store cannot
    /// answer — it threw, or did not answer in time — is <em>not</em> a lost lock: the lease is still ours until the
    /// expiry that was last confirmed (<c>confirmedUntil</c>). The loop keeps the run alive and retries at a tighter
    /// cadence while that expiry is ahead, and abandons the run (cancels it and reports <see cref="HeartbeatOutcome.LockLost"/>)
    /// as soon as the next retry could not land before the lease lapses — so the job is told to stop <em>before</em>
    /// another instance is able to claim the task, never after. With the default <c>LockTtl</c>/2 cadence a single
    /// failure leaves several retries of slack; a real outage still cannot produce a second concurrent run.
    /// </para>
    /// <para>
    /// Every attempt has its own deadline (<see cref="Task.WhenAny(Task[])"/> against a delay) so a store call that
    /// hangs without honouring its token cannot silently outlive the lease.
    /// </para>
    /// </remarks>
    private async Task<HeartbeatOutcome> HeartbeatLoopAsync(
        CronnerJobDescriptor descriptor, CronnerJob job, CancellationTokenSource jobCts, CancellationToken stopHeartbeat,
        CronnerRunState runState, DateTimeOffset confirmedUntil)
    {
        var interval = _options.EffectiveKeepAliveInterval();
        var ttl = _options.LockTtl;
        // After a failed attempt retry sooner than the regular cadence, so several attempts fit into the remaining lease.
        var retry = Min(interval, Max(TimeSpan.FromMilliseconds(250), interval / 4));

        // First renewal: one interval before the confirmed expiry at the latest (a claim that waited before pickup
        // has less lease left), but never later than one interval from now.
        var delay = Clamp(confirmedUntil - DateTimeOffset.UtcNow - interval, TimeSpan.Zero, interval);

        while (true)
        {
            try
            {
                await Task.Delay(delay, stopHeartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return HeartbeatOutcome.Completed; // The run finished, or the host is shutting down.
            }

            var startedAt = Stopwatch.GetTimestamp();
            var now = DateTimeOffset.UtcNow;
            var newExpiry = now + ttl;
            // This attempt must answer before we would have to give up anyway.
            var attemptDeadline = Clamp(confirmedUntil - now - retry, TimeSpan.FromMilliseconds(250), interval);

            bool? renewed;
            try
            {
                renewed = await TryRenewAsync(job.Id, newExpiry, attemptDeadline, stopHeartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopHeartbeat.IsCancellationRequested)
            {
                return HeartbeatOutcome.Completed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DotnetCronner lock renewal failed for task {TaskId}; the lease is confirmed until {ConfirmedUntil:O} and will be retried.", job.Id, confirmedUntil);
                renewed = null;
            }

            if (stopHeartbeat.IsCancellationRequested)
                return HeartbeatOutcome.Completed;

            if (renewed == true)
            {
                confirmedUntil = newExpiry;

                // Fire the keepalive hook WITHOUT awaiting it, so user hook code (a slow query, a GC pause) can
                // never stretch the renewal cadence, cost the lock, or delay finalization. The dispatcher
                // swallows hook exceptions, so the detached task never faults.
                _ = FireLockHookAsync(CronnerHookEvent.KeepAlive, descriptor, job, stopHeartbeat, runState);

                // Hold a fixed renewal cadence regardless of how long the renewal took: the next renewal is one
                // interval after this one began, so the gap between renewals stays ~LockTtl/2 for any job length.
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                delay = elapsed < interval ? interval - elapsed : TimeSpan.Zero;
                continue;
            }

            if (renewed == false)
            {
                // A definitive answer. Either the task was cancelled from elsewhere (the store refuses to renew a
                // Cancelled task) — an ordinary cancel, not a lost lock — or the claim really is no longer ours.
                if (await IsCancelledInStoreAsync(job.Id, stopHeartbeat).ConfigureAwait(false))
                {
                    _logger.LogInformation("DotnetCronner task {TaskId} was cancelled in the store while running; stopping the run.", job.Id);
                    jobCts.Cancel();
                    return HeartbeatOutcome.Cancelled;
                }

                _logger.LogWarning(
                    "DotnetCronner lost the execution lock for task {TaskId}; cancelling the run to prevent a duplicate execution.",
                    job.Id);
                jobCts.Cancel();
                return HeartbeatOutcome.LockLost;
            }

            // Unknown. Keep running while another attempt can still land inside the confirmed lease; otherwise
            // stop now — continuing past the expiry is the one dangerous option.
            if (DateTimeOffset.UtcNow + retry >= confirmedUntil)
            {
                _logger.LogError(
                    "DotnetCronner could not confirm the execution lock for task {TaskId} before its lease lapses at {ConfirmedUntil:O}; abandoning the run so it cannot overlap with a reclaim.",
                    job.Id, confirmedUntil);
                jobCts.Cancel();
                return HeartbeatOutcome.LockLost;
            }

            delay = retry;
        }
    }

    /// <summary>
    /// One renewal attempt with its own deadline. Returns the store's answer, or <c>null</c> when the store did not
    /// answer in time (the orphaned call is observed so it can never surface as an unobserved fault).
    /// </summary>
    private async Task<bool?> TryRenewAsync(string id, DateTimeOffset newExpiry, TimeSpan deadline, CancellationToken stopHeartbeat)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stopHeartbeat);
        var renewTask = _stores.UseAsync(store => store.RenewLockAsync(id, _owner, newExpiry, attemptCts.Token));
        var timeout = Task.Delay(deadline, attemptCts.Token);
        var completed = await Task.WhenAny(renewTask, timeout).ConfigureAwait(false);

        if (completed == renewTask)
        {
            attemptCts.Cancel(); // stop the timer
            return await renewTask.ConfigureAwait(false);
        }

        if (stopHeartbeat.IsCancellationRequested)
            throw new OperationCanceledException(stopHeartbeat);

        attemptCts.Cancel(); // a well-behaved store stops now; a hung one is observed and left behind
        _ = renewTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _logger.LogWarning("DotnetCronner lock renewal for task {TaskId} did not answer within {Deadline}; treating it as unconfirmed.", id, deadline);
        return null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private async Task FinalizeConcurrentAsync(string id, Exception? failure, CancellationToken cancellationToken)
    {
        // The schedule already moved on; only record the outcome without clobbering it.
        var latest = await _stores.UseAsync(store => store.GetByIdAsync(id, cancellationToken)).ConfigureAwait(false);
        if (latest is null)
            return;

        latest.RunCount++;
        latest.LastRunUtc = DateTimeOffset.UtcNow;
        latest.LastError = failure?.Message;
        if (latest.NextRunUtc is null && latest.State == CronnerTaskState.Running)
            latest.State = failure is not null ? CronnerTaskState.Failed : CronnerTaskState.Completed;

        await SafeUpsertAsync(latest, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyOutcome(
        CronnerJob job, CronnerJobDescriptor descriptor, bool cancelledByClient, Exception? failure, DateTimeOffset dueTime)
    {
        var now = DateTimeOffset.UtcNow;

        if (cancelledByClient)
        {
            job.State = CronnerTaskState.Cancelled;
            job.NextRunUtc = null;
            return;
        }

        // Queue mode catches up missed occurrences by scheduling from the consumed due time; the default
        // (DropAndForget) skips ahead to the next occurrence after now.
        var scheduleFrom = descriptor.Concurrency == CronnerConcurrencyMode.Queue ? dueTime : now;
        var oneOff = job.Kind == CronnerJobKind.OneOff;

        if (failure is not null)
        {
            job.LastError = failure.Message;
            if (job.RetryCount < _options.DefaultMaxRetries)
            {
                job.RetryCount++;
                job.NextRunUtc = now + _options.RetryDelay;
                job.State = CronnerTaskState.Scheduled;
                return;
            }

            job.RetryCount = 0;
            // A one-off runs once — never reschedule it by cron; it is finished (failed).
            job.NextRunUtc = oneOff ? null : _calculator.GetNextOccurrence(descriptor.CronString, scheduleFrom, _options.TimeZone);
            job.State = job.NextRunUtc is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Failed;
            return;
        }

        job.LastError = null;
        job.RetryCount = 0;
        job.NextRunUtc = oneOff ? null : _calculator.GetNextOccurrence(descriptor.CronString, scheduleFrom, _options.TimeZone);
        job.State = job.NextRunUtc is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Completed;
    }

    private static void ReleaseLock(CronnerJob job)
    {
        job.LockOwner = null;
        job.LockedUntilUtc = null;
    }

    private async Task SafeUpsertAsync(CronnerJob job, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.UpsertAsync(job, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist state for DotnetCronner task {TaskId}.", job.Id);
        }
    }

    private async Task SafeReleaseLockAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.ReleaseLockAsync(id, _owner, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not fatal: the lock lapses on its own after LockTtl.
            _logger.LogWarning(ex, "DotnetCronner failed to release the execution lock for task {TaskId}; it will expire on its own.", id);
        }
    }

    private async Task SafeFinalizeOrphanedExecutionsAsync(string jobId, DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _stores
                .UseAsync(store => store.FinalizeOrphanedExecutionsAsync(jobId, finishedAt, CronnerExecutionErrors.Orphaned, cancellationToken))
                .ConfigureAwait(false);
            if (count > 0)
                _logger.LogWarning("DotnetCronner finalized {Count} orphaned execution record(s) of task {TaskId} (their owner never finished them).", count, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner failed to finalize orphaned execution records for task {TaskId}.", jobId);
        }
    }

    private async Task SafeUpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.UpdateProgressAsync(id, progress, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DotnetCronner failed to persist progress for task {TaskId}.", id);
        }
    }

    private async Task SafePruneAsync(string definitionId, int keepNewest, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.PruneCompletedOneOffsAsync(definitionId, keepNewest, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner one-off retention pruning failed for definition {DefinitionId}.", definitionId);
        }
    }

    private async Task SafeRecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.RecordExecutionStartedAsync(execution, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner failed to record execution start for task {TaskId}.", execution.JobId);
        }
    }

    private async Task SafeRecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.RecordExecutionFinishedAsync(execution, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner failed to record execution finish for task {TaskId}.", execution.JobId);
        }
    }

    private async Task SafePruneExecutionsAsync(string jobId, int keepNewest, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.PruneExecutionsAsync(jobId, keepNewest, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DotnetCronner execution-history pruning failed for task {TaskId}.", jobId);
        }
    }

    private async Task SafeStoreStartAsync(CronnerJob job, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.OnStartAsync(job, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DotnetCronner store OnStart failed for task {TaskId}.", job.Id);
        }
    }

    private async Task SafeStoreCloseAsync(CronnerJob job, CancellationToken cancellationToken)
    {
        try
        {
            await _stores.UseAsync(store => store.OnCloseAsync(job, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DotnetCronner store OnClose failed for task {TaskId}.", job.Id);
        }
    }
}
