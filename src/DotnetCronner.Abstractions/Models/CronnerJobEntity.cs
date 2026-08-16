namespace DotnetCronner;

/// <summary>
/// A ready-to-map persistence shape for a <see cref="CronnerJob"/>. The EF Core store maps this type, and
/// custom stores may reuse it instead of hand-rolling their own record. It is deliberately a plain,
/// mutable class with a parameterless constructor and <c>virtual</c> properties, so ORMs that build
/// lazy-loading proxies (e.g. NHibernate) can subclass it; EF Core and Dapper work with it unchanged.
/// You can also derive from it to add your own columns.
/// </summary>
public class CronnerJobEntity
{
    /// <summary>Stable unique id (primary key).</summary>
    public virtual string Id { get; set; } = default!;

    /// <summary>Fully qualified task name.</summary>
    public virtual string Name { get; set; } = default!;

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

    /// <summary>Maps this entity to the domain model.</summary>
    public CronnerJob ToDomain() => new()
    {
        Id = Id,
        Name = Name,
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
        var entity = new CronnerJobEntity { Id = job.Id };
        entity.Apply(job);
        return entity;
    }

    /// <summary>Copies the fields of <paramref name="job"/> onto this entity.</summary>
    public void Apply(CronnerJob job)
    {
        Name = job.Name;
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
