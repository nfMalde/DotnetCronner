namespace DotnetCronner;

/// <summary>The default in-process implementation of <see cref="ICronnerClient"/> over the configured store.</summary>
public sealed class CronnerClient : ICronnerClient
{
    private readonly ICronnerStore _store;
    private readonly CronnerScheduleSignal _signal;
    private readonly CronnerExecutionTracker _tracker;

    /// <summary>Creates the client.</summary>
    public CronnerClient(ICronnerStore store, CronnerScheduleSignal signal, CronnerExecutionTracker tracker)
    {
        _store = store;
        _signal = signal;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public Task<CronnerJob?> GetTaskByIdAsync(string id, CancellationToken cancellationToken = default) =>
        _store.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJob>> GetTasksAsync(
        CronnerTaskState? state = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        _store.GetAsync(state, offset, limit, cancellationToken);

    /// <inheritdoc />
    public async Task ScheduleTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                  ?? throw new CronnerTaskNotFoundException(id);

        // If a run is already in progress, queue the trigger so it fires right after — never overlap.
        if (_tracker.IsRunning(id))
        {
            _tracker.MarkTriggerPending(id);
            return;
        }

        job.NextRunUtc = DateTimeOffset.UtcNow;
        job.State = CronnerTaskState.Scheduled;
        await _store.UpsertAsync(job, cancellationToken).ConfigureAwait(false);

        _signal.Signal();
    }

    /// <inheritdoc />
    public async Task CancelTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                  ?? throw new CronnerTaskNotFoundException(id);

        // Signal a running execution to stop; the worker will persist the final Cancelled state.
        // If it isn't running, unschedule it here.
        if (!_tracker.TryCancel(id))
        {
            job.State = CronnerTaskState.Cancelled;
            job.NextRunUtc = null;
            job.LockOwner = null;
            job.LockedUntilUtc = null;
            await _store.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }
}
