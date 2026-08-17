namespace DotnetCronner;

/// <summary>
/// The persisted record of a scheduled task. This is the unit of data that flows through an
/// <see cref="ICronnerStore"/>. It intentionally contains only serializable state — the actual
/// method to invoke lives in the in-memory registry and is looked up by <see cref="Id"/>.
/// </summary>
public sealed class CronnerJob
{
    /// <summary>Stable unique identifier of the task (for a one-off instance, a generated per-enqueue id).</summary>
    public required string Id { get; set; }

    /// <summary>Human readable name, typically the fully qualified <c>Type.Method</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Whether this is a recurring definition or a one-off enqueued instance. Defaults to <see cref="CronnerJobKind.Recurring"/>.</summary>
    public CronnerJobKind Kind { get; set; } = CronnerJobKind.Recurring;

    /// <summary>
    /// For a one-off instance, the id of the registered task definition whose method it runs. Recurring
    /// jobs leave this <c>null</c> and are resolved by <see cref="Id"/>.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>The JSON-serialized payload for a one-off instance, or <c>null</c>.</summary>
    public string? Payload { get; set; }

    /// <summary>The payload's declared type name (for diagnostics and deserialization), or <c>null</c>.</summary>
    public string? PayloadType { get; set; }

    /// <summary>The most recent total progress reported (0..1 by convention). Persisted so the store can surface it.</summary>
    public decimal Progress { get; set; }

    /// <summary>
    /// The cron expression that drives scheduling, or <c>null</c> for a manual / one-shot task that
    /// only runs when triggered via <see cref="ICronnerClient.ScheduleTaskAsync"/>.
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>The current lifecycle state.</summary>
    public CronnerTaskState State { get; set; } = CronnerTaskState.Idle;

    /// <summary>The task's execution priority. Higher priorities are claimed and dispatched first.</summary>
    public CronnerTaskPriority Priority { get; set; } = CronnerTaskPriority.Normal;

    /// <summary>When the task is next due to run (UTC), or <c>null</c> if not scheduled.</summary>
    public DateTimeOffset? NextRunUtc { get; set; }

    /// <summary>When the task last started running (UTC).</summary>
    public DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>Total number of completed executions (successful or failed).</summary>
    public int RunCount { get; set; }

    /// <summary>Number of consecutive retries for the current pending run.</summary>
    public int RetryCount { get; set; }

    /// <summary>The error message from the most recent failed execution, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Identifier of the worker/instance that currently holds the execution lock.</summary>
    public string? LockOwner { get; set; }

    /// <summary>When the current execution lock expires (UTC). A stale lock allows another worker to reclaim the job.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>When the job record was first created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the job record was last updated (UTC).</summary>
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Creates a shallow copy so stores can hand out snapshots without exposing their internal state.</summary>
    public CronnerJob Clone() => (CronnerJob)MemberwiseClone();
}
