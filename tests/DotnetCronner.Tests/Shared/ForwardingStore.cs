namespace DotnetCronner.Tests.Shared;

/// <summary>
/// A store that forwards every <see cref="ICronnerStore"/> member — including the default-implemented ones — to an
/// inner store. Registered as a singleton so a host can be pointed at a specific store instance via
/// <c>UseStore&lt;ForwardingStore&gt;()</c>; subclasses override single members to inject faults.
/// </summary>
public class ForwardingStore : ICronnerStore
{
    public ForwardingStore(ICronnerStore inner) => Inner = inner;

    public ICronnerStore Inner { get; }

    public virtual Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) => Inner.GetByIdAsync(id, ct);
    public virtual Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default) => Inner.GetAsync(state, offset, limit, ct);
    public virtual Task UpsertAsync(CronnerJob job, CancellationToken ct = default) => Inner.UpsertAsync(job, ct);
    public virtual Task RemoveAsync(string id, CancellationToken ct = default) => Inner.RemoveAsync(id, ct);
    public virtual Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default) => Inner.AcquireDueAsync(now, owner, lockTtl, max, ct);
    public virtual Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default) => Inner.RenewLockAsync(id, owner, lockedUntil, ct);
    public virtual Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken ct = default) => Inner.ReleaseLockAsync(id, owner, ct);
    public virtual Task OnStartAsync(CronnerJob job, CancellationToken ct = default) => Inner.OnStartAsync(job, ct);
    public virtual Task OnCloseAsync(CronnerJob job, CancellationToken ct = default) => Inner.OnCloseAsync(job, ct);
    public virtual Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken ct = default) => Inner.PruneCompletedOneOffsAsync(definitionId, keepNewest, ct);
    public virtual Task UpdateProgressAsync(string id, decimal progress, CancellationToken ct = default) => Inner.UpdateProgressAsync(id, progress, ct);
    public virtual Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken ct = default) => Inner.RecordExecutionStartedAsync(execution, ct);
    public virtual Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken ct = default) => Inner.RecordExecutionFinishedAsync(execution, ct);
    public virtual Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(string jobId, int limit, CancellationToken ct = default) => Inner.GetExecutionsAsync(jobId, limit, ct);
    public virtual Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken ct = default) => Inner.PruneExecutionsAsync(jobId, keepNewest, ct);
    public virtual Task<int> FinalizeOrphanedExecutionsAsync(string jobId, DateTimeOffset finishedAt, string error, CancellationToken ct = default) => Inner.FinalizeOrphanedExecutionsAsync(jobId, finishedAt, error, ct);
}

/// <summary>
/// A forwarding store with a kill switch: once <see cref="Kill"/> is called every store call throws, which is what
/// an instance that lost its database (or whose process is about to die) looks like from the scheduler's side.
/// </summary>
public sealed class KillableStore : ForwardingStore
{
    private volatile bool _dead;

    public KillableStore(ICronnerStore inner) : base(inner) { }

    public bool IsDead => _dead;

    public void Kill() => _dead = true;

    private void ThrowIfDead()
    {
        if (_dead)
            throw new InvalidOperationException("The store connection is dead (simulated crash).");
    }

    public override Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) { ThrowIfDead(); return base.GetByIdAsync(id, ct); }
    public override Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default) { ThrowIfDead(); return base.GetAsync(state, offset, limit, ct); }
    public override Task UpsertAsync(CronnerJob job, CancellationToken ct = default) { ThrowIfDead(); return base.UpsertAsync(job, ct); }
    public override Task RemoveAsync(string id, CancellationToken ct = default) { ThrowIfDead(); return base.RemoveAsync(id, ct); }
    public override Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default) { ThrowIfDead(); return base.AcquireDueAsync(now, owner, lockTtl, max, ct); }
    public override Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default) { ThrowIfDead(); return base.RenewLockAsync(id, owner, lockedUntil, ct); }
    public override Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken ct = default) { ThrowIfDead(); return base.ReleaseLockAsync(id, owner, ct); }
    public override Task UpdateProgressAsync(string id, decimal progress, CancellationToken ct = default) { ThrowIfDead(); return base.UpdateProgressAsync(id, progress, ct); }
    public override Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken ct = default) { ThrowIfDead(); return base.RecordExecutionStartedAsync(execution, ct); }
    public override Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken ct = default) { ThrowIfDead(); return base.RecordExecutionFinishedAsync(execution, ct); }
    public override Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(string jobId, int limit, CancellationToken ct = default) { ThrowIfDead(); return base.GetExecutionsAsync(jobId, limit, ct); }
    public override Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken ct = default) { ThrowIfDead(); return base.PruneExecutionsAsync(jobId, keepNewest, ct); }
    public override Task<int> FinalizeOrphanedExecutionsAsync(string jobId, DateTimeOffset finishedAt, string error, CancellationToken ct = default) { ThrowIfDead(); return base.FinalizeOrphanedExecutionsAsync(jobId, finishedAt, error, ct); }
}
