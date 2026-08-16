namespace DotnetCronner;

/// <summary>
/// The lifecycle state of a scheduled task.
/// </summary>
public enum CronnerTaskState
{
    /// <summary>Registered but not currently scheduled to run (e.g. a manual/one-shot task that has no pending run).</summary>
    Idle = 0,

    /// <summary>Scheduled with a future <see cref="CronnerJob.NextRunUtc"/>.</summary>
    Scheduled = 1,

    /// <summary>Claimed by a worker and waiting in the execution queue.</summary>
    Queued = 2,

    /// <summary>Currently executing.</summary>
    Running = 3,

    /// <summary>The most recent execution completed successfully.</summary>
    Completed = 4,

    /// <summary>The most recent execution threw an exception.</summary>
    Failed = 5,

    /// <summary>Execution was cancelled and the task has been unscheduled.</summary>
    Cancelled = 6,
}
