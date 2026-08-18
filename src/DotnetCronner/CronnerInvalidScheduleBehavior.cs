namespace DotnetCronner;

/// <summary>
/// What the scheduler does when a task's cron expression parses cleanly but can never produce a next
/// occurrence (e.g. <c>0 0 31 2 *</c> — 31 February).
/// </summary>
public enum CronnerInvalidScheduleBehavior
{
    /// <summary>
    /// Log an error and mark that one task <see cref="CronnerTaskState.Failed"/>, leaving every other task
    /// to schedule normally. Resilient — one bad expression cannot stop the rest. The default.
    /// </summary>
    MarkFailed = 0,

    /// <summary>
    /// Throw during host startup, so the application refuses to boot until the expression is fixed. Fail-fast
    /// — turns the worst silent failure mode into a loud startup error.
    /// </summary>
    Throw = 1,
}
