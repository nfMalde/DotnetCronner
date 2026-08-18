namespace DotnetCronner;

/// <summary>
/// The EF Core persistence row for one <see cref="CronnerJobExecution"/>. It adds what the abstract
/// <see cref="JobExecutionEntity"/> deliberately leaves out: a numeric identity primary key
/// (<see cref="Id"/>) — the surrogate-key convention most apps use — its link back to the owning job
/// (<see cref="TaskId"/>, a foreign key to <see cref="CronnerJobEntity.TaskId"/>), and the scheduler's
/// per-run <see cref="CorrelationId"/> used to finalize the row that was inserted at start. Derive from it
/// and override the virtual members to add your own columns.
/// </summary>
public class CronnerJobExecutionEntity : JobExecutionEntity
{
    /// <summary>Auto-incrementing surrogate primary key.</summary>
    public virtual long Id { get; set; }

    /// <summary>The owning job's id (<see cref="CronnerJobEntity.TaskId"/>) — the foreign key to the job row.</summary>
    public virtual string TaskId { get; set; } = default!;

    /// <summary>
    /// The scheduler's per-run correlation id (the value of <see cref="CronnerJobExecution.Id"/>). The store
    /// finalizes a run by looking the row up on this, so it is indexed.
    /// </summary>
    public virtual string CorrelationId { get; set; } = default!;

    /// <inheritdoc />
    public override void Apply(CronnerJobExecution execution)
    {
        base.Apply(execution);
        TaskId = execution.JobId;
        CorrelationId = execution.Id;
    }

    /// <summary>
    /// Maps this row to the domain model. Override in a subclass (call <c>base.ToDomain()</c>) to add custom
    /// conversions; it dispatches virtually, so EF-materialized subclasses use your override.
    /// </summary>
    public virtual CronnerJobExecution ToDomain() => ToDomain(CorrelationId, TaskId);

    /// <summary>Creates an entity from a domain <see cref="CronnerJobExecution"/>.</summary>
    public static CronnerJobExecutionEntity From(CronnerJobExecution execution)
    {
        var entity = new CronnerJobExecutionEntity();
        entity.Apply(execution);
        return entity;
    }
}
