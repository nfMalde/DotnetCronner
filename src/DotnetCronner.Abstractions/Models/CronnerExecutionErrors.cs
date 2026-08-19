namespace DotnetCronner;

/// <summary>
/// The well-known <see cref="CronnerJobExecution.Error"/> texts the scheduler writes onto execution-history records
/// it finalizes itself (as opposed to an exception message from the task). Compare against these to filter history.
/// </summary>
public static class CronnerExecutionErrors
{
    /// <summary>
    /// Written onto a <see cref="JobExecutionStatus.Running"/> record that was never finalized by its owner — the
    /// owning scheduler instance crashed or lost its lease — when the task next runs. The record ends as
    /// <see cref="JobExecutionStatus.Failed"/>.
    /// </summary>
    public const string Orphaned = "Orphaned: the owning scheduler instance never finalized this run (crash or lost lease).";

    /// <summary>
    /// Written by the worker that abandoned a run because its execution lock was lost or could no longer be
    /// confirmed before it lapsed. The record ends as <see cref="JobExecutionStatus.Cancelled"/>.
    /// </summary>
    public const string LockLost = "Execution lock lost; the run was abandoned to prevent a concurrent execution.";
}
