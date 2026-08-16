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
    private readonly ICronnerStore _store;
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

    /// <summary>Creates the hosted service.</summary>
    public CronnerHostedService(
        IServiceProvider services,
        ICronnerStore store,
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
        _store = store;
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

        await SeedAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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
        foreach (var descriptor in _registry.Descriptors)
        {
            try
            {
                var existing = await _store.GetByIdAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    var next = _calculator.GetNextOccurrence(descriptor.CronString, now, _options.TimeZone);
                    await _store.UpsertAsync(new CronnerJob
                    {
                        Id = descriptor.Id,
                        Name = descriptor.Name,
                        CronExpression = descriptor.CronString,
                        Priority = descriptor.Priority,
                        NextRunUtc = next,
                        State = next is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Idle,
                    }, cancellationToken).ConfigureAwait(false);
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
                        existing.State = existing.NextRunUtc is not null ? CronnerTaskState.Scheduled : existing.State;
                    }

                    await _store.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed DotnetCronner task {TaskId}.", descriptor.Id);
            }
        }
    }

    private async Task PollLoopAsync(ChannelWriter<CronnerJob> writer, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await _store
                    .AcquireDueAsync(DateTimeOffset.UtcNow, _owner, _options.LockTtl, _options.MaxConcurrentTasks, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var job in due)
                    await writer.WriteAsync(job, stoppingToken).ConfigureAwait(false);
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
                await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
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
        _registry.TryGet(job.Id, out var descriptor);

        // The claim happened in the poll loop; announce it as the worker takes the job.
        await FireLockHookAsync(CronnerHookEvent.LockAcquire, descriptor, job, stoppingToken).ConfigureAwait(false);

        if (descriptor is null)
        {
            _logger.LogWarning("No descriptor registered for task {TaskId}; unscheduling it.", job.Id);
            job.State = CronnerTaskState.Failed;
            job.LastError = "No descriptor registered for this task.";
            job.NextRunUtc = null;
            ReleaseLock(job);
            await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
            await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken).ConfigureAwait(false);
            return;
        }

        var dueTime = job.NextRunUtc ?? DateTimeOffset.UtcNow;
        var concurrent = descriptor.Concurrency == CronnerConcurrencyMode.Concurrent;

        // In concurrent mode, advance the schedule up front so the next occurrence can run in parallel.
        if (concurrent)
        {
            job.NextRunUtc = _calculator.GetNextOccurrence(descriptor.CronString, DateTimeOffset.UtcNow, _options.TimeZone);
            job.State = job.NextRunUtc is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Running;
            ReleaseLock(job);
            await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
            await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken).ConfigureAwait(false);
        }
        else
        {
            job.State = CronnerTaskState.Running;
            job.LastRunUtc = DateTimeOffset.UtcNow;
            await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _tracker.Register(job.Id, jobCts);

        // Concurrent mode already released the lock up front, so it needs no keepalive. Every lock-holding
        // mode gets a heartbeat that renews the claim while the task runs; if the renewal ever fails the
        // lock was lost (a stall reclaim elsewhere) and the run is cancelled to avoid a duplicate execution.
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = concurrent
            ? Task.FromResult(false)
            : HeartbeatLoopAsync(descriptor, job, jobCts, heartbeatCts.Token);

        bool cancelledByClient;
        Exception? failure;
        bool lockLost;
        try
        {
            (cancelledByClient, failure) = await RunWithHooksAsync(descriptor, job, jobCts, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            lockLost = await heartbeat.ConfigureAwait(false);
            _tracker.Unregister(job.Id);
        }

        if (concurrent)
        {
            await FinalizeConcurrentAsync(job.Id, failure, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (lockLost)
        {
            // The claim lapsed mid-run and another worker reclaimed the task; it owns the schedule now.
            // Writing our (stale) outcome would clobber the reclaiming worker's state, so we stop here.
            _logger.LogWarning(
                "DotnetCronner task {TaskId} lost its execution lock during the run and was abandoned; the reclaiming worker owns it now.",
                job.Id);
            await FireLockHookAsync(CronnerHookEvent.LockLost, descriptor, job, stoppingToken).ConfigureAwait(false);
            return;
        }

        job.RunCount++;
        ReleaseLock(job);
        ApplyOutcome(job, descriptor, cancelledByClient, failure, dueTime);

        // A manual trigger that arrived mid-run takes precedence and runs immediately.
        if (!cancelledByClient && _tracker.ConsumePendingTrigger(job.Id))
        {
            job.NextRunUtc = DateTimeOffset.UtcNow;
            job.State = CronnerTaskState.Scheduled;
        }

        await SafeUpsertAsync(job, stoppingToken).ConfigureAwait(false);
        await FireLockHookAsync(CronnerHookEvent.LockRelease, descriptor, job, stoppingToken).ConfigureAwait(false);

        if (job.NextRunUtc is { } next && next <= DateTimeOffset.UtcNow)
            _signal.Signal();
    }

    private Task FireLockHookAsync(
        CronnerHookEvent hookEvent, CronnerJobDescriptor? descriptor, CronnerJob job, CancellationToken cancellationToken) =>
        _hooks.DispatchAsync(hookEvent, _jobProvider, descriptor, job, TimeSpan.Zero, null, cancellationToken);

    private async Task<(bool Cancelled, Exception? Failure)> RunWithHooksAsync(
        CronnerJobDescriptor descriptor, CronnerJob job, CancellationTokenSource jobCts, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _hooks.DispatchAsync(CronnerHookEvent.Start, _jobProvider, descriptor, job, TimeSpan.Zero, null, stoppingToken)
            .ConfigureAwait(false);

        var cancelled = false;
        Exception? failure = null;

        // The task runs in its own scope, separate from the hooks' scopes.
        await using (var scope = _jobProvider.CreateAsyncScope())
        {
            // Wire the progress context the task resolves from this same scope, so its reports reach the hooks.
            var progress = scope.ServiceProvider.GetService<CronnerJobContext>();
            progress?.Initialize((hookEvent, total, scopeInfo) =>
                _hooks.DispatchAsync(hookEvent, _jobProvider, descriptor, job, TimeSpan.Zero, null, stoppingToken, total, scopeInfo));

            try
            {
                await CronnerJobInvoker.InvokeAsync(scope.ServiceProvider, descriptor, jobCts.Token).ConfigureAwait(false);
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
        }

        var terminal = cancelled
            ? CronnerHookEvent.Cancel
            : failure is not null ? CronnerHookEvent.Fail : CronnerHookEvent.Success;
        await _hooks.DispatchAsync(terminal, _jobProvider, descriptor, job, stopwatch.Elapsed, failure, stoppingToken)
            .ConfigureAwait(false);

        return (cancelled, failure);
    }

    /// <summary>
    /// Keeps the execution lock alive while a task runs. Renews about halfway through the TTL so a slow
    /// store call or a GC pause has slack before the lock lapses. Returns <c>true</c> if the lock was lost
    /// (the run was cancelled to prevent a duplicate execution); <c>false</c> if it stopped normally.
    /// </summary>
    private async Task<bool> HeartbeatLoopAsync(
        CronnerJobDescriptor descriptor, CronnerJob job, CancellationTokenSource jobCts, CancellationToken stopHeartbeat)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, _options.LockTtl.TotalMilliseconds / 2));
        var delay = interval;
        while (true)
        {
            try
            {
                await Task.Delay(delay, stopHeartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false; // The run finished, or the host is shutting down.
            }

            var startedAt = Stopwatch.GetTimestamp();

            bool renewed;
            try
            {
                renewed = await _store
                    .RenewLockAsync(job.Id, _owner, DateTimeOffset.UtcNow + _options.LockTtl, stopHeartbeat)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopHeartbeat.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                // A transient store error must not tear down the heartbeat; try again next tick.
                _logger.LogWarning(ex, "DotnetCronner lock renewal failed for task {TaskId}; will retry.", job.Id);
                delay = interval;
                continue;
            }

            if (!renewed)
            {
                _logger.LogWarning(
                    "DotnetCronner lost the execution lock for task {TaskId}; cancelling the run to prevent a duplicate execution.",
                    job.Id);
                jobCts.Cancel();
                return true;
            }

            // Fire the keepalive hook WITHOUT awaiting it, so user hook code (a slow query, a GC pause) can
            // never stretch the renewal cadence, cost the lock, or delay finalization. The dispatcher
            // swallows hook exceptions, so the detached task never faults.
            _ = FireLockHookAsync(CronnerHookEvent.KeepAlive, descriptor, job, stopHeartbeat);

            // Hold a fixed renewal cadence regardless of how long the renewal took: the next renewal is one
            // interval after this one began, so the gap between renewals stays ~LockTtl/2 for any job length.
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            delay = elapsed < interval ? interval - elapsed : TimeSpan.Zero;
        }
    }

    private async Task FinalizeConcurrentAsync(string id, Exception? failure, CancellationToken cancellationToken)
    {
        // The schedule already moved on; only record the outcome without clobbering it.
        var latest = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
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
            job.NextRunUtc = _calculator.GetNextOccurrence(descriptor.CronString, scheduleFrom, _options.TimeZone);
            job.State = job.NextRunUtc is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Failed;
            return;
        }

        job.LastError = null;
        job.RetryCount = 0;
        job.NextRunUtc = _calculator.GetNextOccurrence(descriptor.CronString, scheduleFrom, _options.TimeZone);
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
            await _store.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist state for DotnetCronner task {TaskId}.", job.Id);
        }
    }

    private async Task SafeStoreStartAsync(CronnerJob job, CancellationToken cancellationToken)
    {
        try
        {
            await _store.OnStartAsync(job, cancellationToken).ConfigureAwait(false);
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
            await _store.OnCloseAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DotnetCronner store OnClose failed for task {TaskId}.", job.Id);
        }
    }
}
