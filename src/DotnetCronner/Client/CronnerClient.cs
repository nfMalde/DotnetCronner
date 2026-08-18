namespace DotnetCronner;

/// <summary>The default in-process implementation of <see cref="ICronnerClient"/> over the configured store.</summary>
public sealed class CronnerClient : ICronnerClient
{
    private readonly CronnerStoreAccessor _stores;
    private readonly CronnerScheduleSignal _signal;
    private readonly CronnerExecutionTracker _tracker;
    private readonly CronnerRegistry _registry;

    /// <summary>Creates the client.</summary>
    public CronnerClient(CronnerStoreAccessor stores, CronnerScheduleSignal signal, CronnerExecutionTracker tracker, CronnerRegistry registry)
    {
        _stores = stores;
        _signal = signal;
        _tracker = tracker;
        _registry = registry;
    }

    /// <inheritdoc />
    public Task<CronnerJob?> GetTaskByIdAsync(string id, CancellationToken cancellationToken = default) =>
        _stores.UseAsync(store => store.GetByIdAsync(id, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJob>> GetTasksAsync(
        CronnerTaskState? state = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        _stores.UseAsync(store => store.GetAsync(state, offset, limit, cancellationToken));

    /// <inheritdoc />
    public IReadOnlyList<CronnerRegisteredTask> GetRegisteredTasks() =>
        _registry.Descriptors
            .Select(d => new CronnerRegisteredTask
            {
                Id = d.Id,
                Name = d.Name,
                CronExpression = d.CronString,
                Priority = d.Priority,
                Concurrency = d.Concurrency,
                Description = d.Description,
                IsManual = d.CronString is null,
            })
            .ToArray();

    /// <inheritdoc />
    public async Task TriggerNowAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _stores.UseAsync(store => store.GetByIdAsync(id, cancellationToken)).ConfigureAwait(false)
                  ?? throw new CronnerTaskNotFoundException(id);

        // If a run is already in progress, queue the trigger so it fires right after — never overlap.
        if (_tracker.IsRunning(id))
        {
            _tracker.MarkTriggerPending(id);
            return;
        }

        job.NextRunUtc = DateTimeOffset.UtcNow;
        job.State = CronnerTaskState.Scheduled;
        await _stores.UseAsync(store => store.UpsertAsync(job, cancellationToken)).ConfigureAwait(false);

        _signal.Signal();
    }

    /// <inheritdoc />
    public Task ScheduleTaskAsync(string id, CancellationToken cancellationToken = default) =>
        TriggerNowAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<string> EnqueueAsync<TPayload>(
        string taskId,
        TPayload payload,
        CronnerTaskPriority priority = CronnerTaskPriority.Normal,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(taskId, out var descriptor))
            throw new CronnerTaskNotFoundException(taskId);

        var now = DateTimeOffset.UtcNow;
        var due = runAt ?? now;
        var instanceId = $"{taskId}#{Guid.NewGuid():N}";

        var job = new CronnerJob
        {
            Id = instanceId,
            Name = descriptor.Name,
            Kind = CronnerJobKind.OneOff,
            DefinitionId = taskId,
            CronExpression = null,
            Priority = priority,
            Payload = CronnerPayloadSerializer.Serialize(payload, typeof(TPayload)),
            PayloadType = typeof(TPayload).FullName,
            NextRunUtc = due,
            State = CronnerTaskState.Scheduled,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        await _stores.UseAsync(store => store.UpsertAsync(job, cancellationToken)).ConfigureAwait(false);

        if (due <= now)
            _signal.Signal();

        return instanceId;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(
        string taskId, int limit = 50, CancellationToken cancellationToken = default) =>
        _stores.UseAsync(store => store.GetExecutionsAsync(taskId, limit, cancellationToken));

    /// <inheritdoc />
    public async Task CancelTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _stores.UseAsync(store => store.GetByIdAsync(id, cancellationToken)).ConfigureAwait(false)
                  ?? throw new CronnerTaskNotFoundException(id);

        // Signal a running execution to stop; the worker will persist the final Cancelled state.
        // If it isn't running, unschedule it here.
        if (!_tracker.TryCancel(id))
        {
            job.State = CronnerTaskState.Cancelled;
            job.NextRunUtc = null;
            job.LockOwner = null;
            job.LockedUntilUtc = null;
            await _stores.UseAsync(store => store.UpsertAsync(job, cancellationToken)).ConfigureAwait(false);
        }
    }
}
