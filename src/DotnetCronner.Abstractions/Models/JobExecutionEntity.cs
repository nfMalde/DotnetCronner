namespace DotnetCronner;

/// <summary>
/// The abstract persistence base for one <see cref="CronnerJobExecution"/> (a single run recorded in the
/// execution history). It carries only the run's <em>intrinsic</em> fields (timestamps, status, attempt,
/// error, owner); each store derives from it to add its own primary key and its link back to the owning job.
/// </summary>
/// <remarks>
/// <para>
/// By design the base has <b>no primary key and no job foreign key</b>. The scheduler never touches an
/// execution's surrogate key — it correlates a run through the string execution id
/// (<see cref="CronnerJobExecution.Id"/>, exposed as <c>ctx.ExecutionId</c>) — so the key's type is entirely
/// the store's/database's choice: the EF Core store uses a numeric identity, another store might use a
/// <see cref="Guid"/> or a string. Putting a fixed-type key here would dictate a choice the abstraction has
/// no stake in. A store subclass adds the key and the job link it needs (the EF store adds a numeric
/// <c>Id</c> + a <c>TaskId</c> foreign key; the Redis store uses the hash key).
/// </para>
/// <para>
/// This mirrors the <see cref="CronnerJobEntity"/> approach: <c>virtual</c> members and a
/// parameterless-constructible shape, so ORMs that build lazy-loading proxies can subclass it, and the
/// mapping helpers are <c>virtual</c> so you can extend them.
/// </para>
/// </remarks>
public abstract class JobExecutionEntity
{
    /// <summary>When the run started (UTC).</summary>
    public virtual DateTimeOffset StartedAt { get; set; }

    /// <summary>When the run finished (UTC), or <c>null</c> while it is still running.</summary>
    public virtual DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The run's status. <see cref="JobExecutionStatus.Running"/> until finalized.</summary>
    public virtual JobExecutionStatus Status { get; set; }

    /// <summary>The 1-based attempt number for this run.</summary>
    public virtual int Attempt { get; set; }

    /// <summary>The error message if the run failed, otherwise <c>null</c>.</summary>
    public virtual string? Error { get; set; }

    /// <summary>Identifier of the scheduler instance that ran this execution ("which node ran this"), or <c>null</c>.</summary>
    public virtual string? Owner { get; set; }

    /// <summary>Optional consumer data attached to this run, as JSON, or <c>null</c>.</summary>
    /// <remarks>
    /// Deprecated — execution history records an execution, not application data. Keep app data/logs in your
    /// own store, correlated by the execution id. Still mapped for now; removed in a future release.
    /// </remarks>
    [Obsolete("Execution history records an execution, not application data — keep app data/logs in your own store, correlated by the execution id (ctx.ExecutionId). Removed in a future release.")]
    public virtual string? Data { get; set; }

    /// <summary>
    /// Copies the run fields of <paramref name="execution"/> onto this entity. Override in a subclass (call
    /// <c>base.Apply(execution)</c>) to also populate your own key and job-link columns.
    /// </summary>
    public virtual void Apply(CronnerJobExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        StartedAt = execution.StartedAt;
        FinishedAt = execution.FinishedAt;
        Status = execution.Status;
        Attempt = execution.Attempt;
        Error = execution.Error;
        Owner = execution.Owner;
#pragma warning disable CS0618 // Data is obsolete; the base still maps it while it is phased out.
        Data = execution.Data;
#pragma warning restore CS0618
    }

    /// <summary>
    /// Builds a domain <see cref="CronnerJobExecution"/> from this entity's run fields. Subclasses call this
    /// from their own public mapping method, supplying the correlation <paramref name="id"/> and owning
    /// <paramref name="jobId"/> they store themselves.
    /// </summary>
    protected CronnerJobExecution ToDomain(string id, string jobId)
    {
#pragma warning disable CS0618 // Data is obsolete; the base still maps it while it is phased out.
        return new()
        {
            Id = id,
            JobId = jobId,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
            Status = Status,
            Attempt = Attempt,
            Error = Error,
            Owner = Owner,
            Data = Data,
        };
#pragma warning restore CS0618
    }
}
