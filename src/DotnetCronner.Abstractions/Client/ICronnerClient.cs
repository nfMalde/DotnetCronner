namespace DotnetCronner;

/// <summary>
/// The in-process management API for DotnetCronner. Inject this into your own (already secured)
/// application to inspect and control tasks — there is deliberately no bundled dashboard app to host
/// or authenticate. Expose whatever slice of this you need through your existing endpoints.
/// </summary>
public interface ICronnerClient
{
    /// <summary>Returns the task with the given <paramref name="id"/>, or <c>null</c> if it does not exist.</summary>
    Task<CronnerJob?> GetTaskByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of tasks that have a store row, optionally filtered by <paramref name="state"/>. A
    /// registered task that has never run has no row yet — use <see cref="GetRegisteredTasks"/> to list
    /// every definition regardless of run history.
    /// </summary>
    Task<IReadOnlyList<CronnerJob>> GetTasksAsync(
        CronnerTaskState? state = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every registered task definition from the in-memory registry — including manual / enqueue-only tasks
    /// that have never executed and therefore have no store row. Intended for admin listings.
    /// </summary>
    IReadOnlyList<CronnerRegisteredTask> GetRegisteredTasks();

    /// <summary>
    /// Runs the task with the given <paramref name="id"/> <b>immediately</b>, ignoring its cron, by marking
    /// it due now and waking the scheduler (if a run is already in progress, it fires again right after —
    /// never overlapping). Throws if the task is unknown. This is the unambiguous "run now" operation.
    /// </summary>
    Task TriggerNowAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Alias of <see cref="TriggerNowAsync"/> — runs the task now, ignoring its cron.</summary>
    Task ScheduleTaskAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a single one-off run of the registered task <paramref name="taskId"/>, carrying a typed
    /// <paramref name="payload"/> that is delivered to the method parameter of type
    /// <typeparamref name="TPayload"/> (all other parameters resolve from DI as usual). It runs at
    /// <paramref name="runAt"/>, or as soon as possible when that is <c>null</c>. Returns the new one-off
    /// instance id.
    /// </summary>
    /// <remarks>
    /// The payload is JSON-serialized as its <em>declared</em> <typeparamref name="TPayload"/>, with cycle
    /// protection — but you should pass plain, serializable DTOs, not lazy-loading ORM proxies/entities.
    /// The target task is typically a <c>[CronnerTask]</c> method with no cron (enqueue-only). Throws if
    /// <paramref name="taskId"/> is not registered.
    /// </remarks>
    Task<string> EnqueueAsync<TPayload>(
        string taskId,
        TPayload payload,
        CronnerTaskPriority priority = CronnerTaskPriority.Normal,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the task with the given <paramref name="id"/>. If it is <b>currently running</b>, its
    /// <see cref="CancellationToken"/> is signalled and the worker persists the terminal <c>Cancelled</c>
    /// state (so long-running executions that honour their token stop). If it is <b>not running</b>, it is
    /// unscheduled (future runs cleared). Throws if the task is unknown.
    /// </summary>
    Task CancelTaskAsync(string id, CancellationToken cancellationToken = default);
}
