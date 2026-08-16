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

    /// <summary>Returns a page of tasks, optionally filtered by <paramref name="state"/>.</summary>
    Task<IReadOnlyList<CronnerJob>> GetTasksAsync(
        CronnerTaskState? state = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers an immediate run of the task with the given <paramref name="id"/> by marking it due now
    /// and waking the scheduler. Throws if the task is unknown.
    /// </summary>
    Task ScheduleTaskAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the task with the given <paramref name="id"/>: signals cancellation to a running
    /// execution and unschedules future runs. Throws if the task is unknown.
    /// </summary>
    Task CancelTaskAsync(string id, CancellationToken cancellationToken = default);
}
