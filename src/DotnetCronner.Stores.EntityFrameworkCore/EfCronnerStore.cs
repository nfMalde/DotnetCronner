using Microsoft.EntityFrameworkCore;

namespace DotnetCronner;

/// <summary>
/// An <see cref="ICronnerStore"/> backed by Entity Framework Core. Uses an
/// <see cref="IDbContextFactory{TContext}"/> so it can run inside the singleton scheduler, and claims due
/// tasks with a single conditional UPDATE that re-asserts eligibility in the statement itself — correct on
/// every provider with no concurrency token required.
/// </summary>
/// <typeparam name="TContext">A <see cref="DbContext"/> that exposes the Cronner tables.</typeparam>
public sealed class EfCronnerStore<TContext> : ICronnerStore
    where TContext : DbContext, ICronnerDbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;

    /// <summary>Creates the store over the given context factory.</summary>
    public EfCronnerStore(IDbContextFactory<TContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.CronnerJobs.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TaskId == id, cancellationToken).ConfigureAwait(false);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<CronnerJobEntity> query = context.CronnerJobs.AsNoTracking();
        if (state is not null)
            query = query.Where(e => e.State == state);

        var entities = await query
            .OrderBy(e => e.CreatedUtc)
            .ThenBy(e => e.TaskId)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return entities.Select(e => e.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.CronnerJobs.FirstOrDefaultAsync(e => e.TaskId == job.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new CronnerJobEntity { TaskId = job.Id };
            entity.Apply(job);
            context.CronnerJobs.Add(entity);
        }
        else
        {
            entity.Apply(job);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.CronnerJobs.FirstOrDefaultAsync(e => e.TaskId == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return;

        context.CronnerJobs.Remove(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        var lockedUntil = now + lockTtl;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await context.CronnerJobs.AsNoTracking()
            .Where(e => e.State != CronnerTaskState.Cancelled && e.NextRunUtc != null && e.NextRunUtc <= now &&
                        (e.LockOwner == null || e.LockedUntilUtc == null || e.LockedUntilUtc < now))
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.NextRunUtc)
            .Take(max)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var claimed = new List<CronnerJob>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Atomic claim: the UPDATE re-asserts eligibility, so it only wins if the row is still free.
            // If another instance (or an earlier iteration) took it, ExecuteUpdate affects 0 rows.
            var affected = await context.CronnerJobs
                .Where(e => e.TaskId == candidate.TaskId && e.State != CronnerTaskState.Cancelled &&
                            e.NextRunUtc != null && e.NextRunUtc <= now &&
                            (e.LockOwner == null || e.LockedUntilUtc == null || e.LockedUntilUtc < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.State, CronnerTaskState.Queued)
                    .SetProperty(e => e.LockOwner, owner)
                    .SetProperty(e => e.LockedUntilUtc, lockedUntil)
                    .SetProperty(e => e.UpdatedUtc, DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
                continue;

            candidate.State = CronnerTaskState.Queued;
            candidate.LockOwner = owner;
            candidate.LockedUntilUtc = lockedUntil;
            candidate.UpdatedUtc = DateTimeOffset.UtcNow;
            claimed.Add(candidate.ToDomain());
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Conditional on ownership: the UPDATE only touches the row while this worker still holds the lock,
        // so a claim that was already reclaimed by someone else cannot be resurrected.
        var affected = await context.CronnerJobs
            .Where(e => e.TaskId == id && e.LockOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.LockedUntilUtc, lockedUntil)
                .SetProperty(e => e.UpdatedUtc, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var stale = await context.CronnerJobs
            .Where(e => e.Kind == CronnerJobKind.OneOff && e.DefinitionId == definitionId &&
                        (e.State == CronnerTaskState.Completed || e.State == CronnerTaskState.Failed))
            .OrderByDescending(e => e.UpdatedUtc)
            .Skip(Math.Max(0, keepNewest))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stale.Count == 0)
            return;

        context.CronnerJobs.RemoveRange(stale);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.CronnerJobs
            .Where(e => e.TaskId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Progress, progress), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.CronnerJobExecutions.Add(CronnerJobExecutionEntity.From(execution));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Finalize the row inserted at start (matched on the correlation id) with a targeted update.
        var affected = await context.CronnerJobExecutions
            .Where(e => e.CorrelationId == execution.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.FinishedAt, execution.FinishedAt)
                .SetProperty(e => e.Status, execution.Status)
                .SetProperty(e => e.Error, execution.Error)
                .SetProperty(e => e.Data, execution.Data), cancellationToken)
            .ConfigureAwait(false);

        // If the start record never landed (history was enabled mid-run, or its insert failed), insert the
        // finished record so the run is not lost entirely.
        if (affected == 0)
        {
            context.CronnerJobExecutions.Add(CronnerJobExecutionEntity.From(execution));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(
        string jobId, int limit, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await context.CronnerJobExecutions.AsNoTracking()
            .Where(e => e.TaskId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Max(0, limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return entities.Select(e => e.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var stale = await context.CronnerJobExecutions
            .Where(e => e.TaskId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, keepNewest))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stale.Count == 0)
            return;

        context.CronnerJobExecutions.RemoveRange(stale);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
