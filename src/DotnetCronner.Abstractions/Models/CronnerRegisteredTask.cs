namespace DotnetCronner;

/// <summary>
/// A read-only view of a <em>registered</em> task definition (from the in-memory registry), independent of
/// whether it has ever run or has a store row. Returned by <see cref="ICronnerClient.GetRegisteredTasks"/>
/// so an admin screen can list every job — including manual / enqueue-only ones that have never executed.
/// </summary>
public sealed class CronnerRegisteredTask
{
    /// <summary>The task's stable id.</summary>
    public required string Id { get; init; }

    /// <summary>Fully qualified <c>Type.Method</c> name.</summary>
    public required string Name { get; init; }

    /// <summary>The cron expression, or <c>null</c> for a manual / enqueue-only task.</summary>
    public string? CronExpression { get; init; }

    /// <summary>Execution priority.</summary>
    public CronnerTaskPriority Priority { get; init; }

    /// <summary>Concurrency policy.</summary>
    public CronnerConcurrencyMode Concurrency { get; init; }

    /// <summary>Human-readable description from the attribute or schedule options, if any.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the task has no cron (runs only when triggered by id or enqueued with a payload).</summary>
    public bool IsManual { get; init; }
}
