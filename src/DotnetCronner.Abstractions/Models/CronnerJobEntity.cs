namespace DotnetCronner;

/// <summary>
/// A ready-to-map persistence shape for a <see cref="CronnerJob"/>. The EF Core store maps this type, and
/// custom stores may reuse it instead of hand-rolling their own record. It is deliberately a plain,
/// mutable class with a parameterless constructor and <c>virtual</c> properties, so ORMs that build
/// lazy-loading proxies (e.g. NHibernate) can subclass it; EF Core and Dapper work with it unchanged.
/// You can also derive from it to add your own columns and override the virtual <see cref="ToDomain"/> /
/// <see cref="Apply"/> to map them.
/// </summary>
public class CronnerJobEntity
{
    /// <summary>
    /// The task's stable string id (the value of <see cref="CronnerJob.Id"/>) and the primary key. Named
    /// <c>TaskId</c> rather than <c>Id</c> on purpose, so it never collides with an <c>int</c>/<c>long</c>
    /// surrogate-key convention on your own entities, base classes, or automapper.
    /// </summary>
    public virtual string TaskId { get; set; } = default!;

    /// <summary>Fully qualified task name.</summary>
    public virtual string Name { get; set; } = default!;

    /// <summary>Whether this is a recurring definition or a one-off enqueued instance.</summary>
    public virtual CronnerJobKind Kind { get; set; }

    /// <summary>For a one-off instance, the id of the registered definition whose method it runs.</summary>
    public virtual string? DefinitionId { get; set; }

    /// <summary>The JSON-serialized payload for a one-off instance, or <c>null</c>.</summary>
    public virtual string? Payload { get; set; }

    /// <summary>The payload's declared type name, or <c>null</c>.</summary>
    public virtual string? PayloadType { get; set; }

    /// <summary>The most recent total progress reported (0..1 by convention).</summary>
    public virtual decimal Progress { get; set; }

    /// <summary>Cron expression, or <c>null</c> for a manual task.</summary>
    public virtual string? CronExpression { get; set; }

    /// <summary>Lifecycle state.</summary>
    public virtual CronnerTaskState State { get; set; }

    /// <summary>Execution priority.</summary>
    public virtual CronnerTaskPriority Priority { get; set; }

    /// <summary>Next due time (UTC).</summary>
    public virtual DateTimeOffset? NextRunUtc { get; set; }

    /// <summary>Last start time (UTC).</summary>
    public virtual DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>Total completed executions.</summary>
    public virtual int RunCount { get; set; }

    /// <summary>Consecutive retries for the pending run.</summary>
    public virtual int RetryCount { get; set; }

    /// <summary>Most recent error message.</summary>
    public virtual string? LastError { get; set; }

    /// <summary>Current lock owner.</summary>
    public virtual string? LockOwner { get; set; }

    /// <summary>Lock expiry (UTC).</summary>
    public virtual DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>Record creation time (UTC).</summary>
    public virtual DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Record update time (UTC).</summary>
    public virtual DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>
    /// Maps this entity to the domain model. Override in a subclass (call <c>base.ToDomain()</c>) to add
    /// custom conversions; it dispatches virtually, so EF-materialized subclasses use your override.
    /// </summary>
    public virtual CronnerJob ToDomain() => new()
    {
        Id = TaskId,
        Name = Name,
        Kind = Kind,
        DefinitionId = DefinitionId,
        Payload = Payload,
        PayloadType = PayloadType,
        Progress = Progress,
        CronExpression = CronExpression,
        State = State,
        Priority = Priority,
        NextRunUtc = NextRunUtc,
        LastRunUtc = LastRunUtc,
        RunCount = RunCount,
        RetryCount = RetryCount,
        LastError = LastError,
        LockOwner = LockOwner,
        LockedUntilUtc = LockedUntilUtc,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };

    /// <summary>Creates an entity from a domain <see cref="CronnerJob"/>.</summary>
    public static CronnerJobEntity From(CronnerJob job)
    {
        var entity = new CronnerJobEntity { TaskId = job.Id };
        entity.Apply(job);
        return entity;
    }

    /// <summary>
    /// Copies the fields of <paramref name="job"/> onto this entity. Override in a subclass (call
    /// <c>base.Apply(job)</c>) to also populate your own columns — e.g. stamp a tenant/correlation id.
    /// </summary>
    public virtual void Apply(CronnerJob job)
    {
        Name = job.Name;
        Kind = job.Kind;
        DefinitionId = job.DefinitionId;
        Payload = job.Payload;
        PayloadType = job.PayloadType;
        Progress = job.Progress;
        CronExpression = job.CronExpression;
        State = job.State;
        Priority = job.Priority;
        NextRunUtc = job.NextRunUtc;
        LastRunUtc = job.LastRunUtc;
        RunCount = job.RunCount;
        RetryCount = job.RetryCount;
        LastError = job.LastError;
        LockOwner = job.LockOwner;
        LockedUntilUtc = job.LockedUntilUtc;
        CreatedUtc = job.CreatedUtc;
        UpdatedUtc = job.UpdatedUtc;
    }
}
