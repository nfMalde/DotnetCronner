using Microsoft.EntityFrameworkCore;

namespace DotnetCronner;

/// <summary>
/// An <see cref="ICronnerStore"/> backed by Entity Framework Core. Uses an
/// <see cref="IDbContextFactory{TContext}"/> so it can run inside the singleton scheduler, and claims due
/// tasks with a single conditional UPDATE that re-asserts eligibility in the statement itself — correct on
/// every provider with no concurrency token required. Lock renewal and release are owner-conditional single
/// statements, and <see cref="UpsertAsync"/> never touches an existing row's lock columns, so multiple scheduler
/// instances can share one database safely (validated by contended tests on PostgreSQL and SQL Server).
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
            // Lock columns belong to AcquireDue/RenewLock/ReleaseLock: keep the row's current values so a
            // caller holding a stale snapshot can never clear or shorten a claim taken in the meantime. EF emits
            // no SET for an unchanged column, so even a claim that lands between this read and the save survives.
            var lockOwner = entity.LockOwner;
            var lockedUntil = entity.LockedUntilUtc;
            entity.Apply(job);
            entity.LockOwner = lockOwner;
            entity.LockedUntilUtc = lockedUntil;
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
        if (max <= 0)
            return Array.Empty<CronnerJob>();

        var lockedUntil = now + lockTtl;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var claimed = new List<CronnerJob>(max);

        // Candidates are read without locks and then claimed one by one with a conditional UPDATE that re-asserts
        // eligibility in the statement itself. That single statement is the atomic claim: on PostgreSQL, SQL
        // Server, MySQL and SQLite an UPDATE evaluates its WHERE against the committed row (waiting for a
        // concurrent writer first), so when two instances race for the same row exactly one UPDATE affects it.
        // No transaction, no SKIP LOCKED and no concurrency token are needed for correctness.
        //
        // When other instances win every candidate of a page, re-read (bounded): the rows they took are no longer
        // eligible, so the next read starts at the first row still free and an instance with free capacity is
        // not starved by a head-of-line full of rows that were just taken.
        const int maxRounds = 4;
        for (var round = 0; round < maxRounds && claimed.Count < max; round++)
        {
            var wanted = max - claimed.Count;
            var candidates = await context.CronnerJobs.AsNoTracking()
                .Where(e => e.State != CronnerTaskState.Cancelled && e.NextRunUtc != null && e.NextRunUtc <= now &&
                            (e.LockOwner == null || e.LockedUntilUtc == null || e.LockedUntilUtc < now))
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.NextRunUtc)
                .ThenBy(e => e.TaskId)
                .Take(wanted)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

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

            // A short page means there is nothing further to look at.
            if (candidates.Count < wanted)
                break;
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Conditional on ownership: the UPDATE only touches the row while this worker still holds the lock,
        // so a claim that was already reclaimed by someone else cannot be resurrected. A Cancelled task is not
        // renewed either — the engine reads that as "stop this run" (a cancel issued from another instance).
        var affected = await context.CronnerJobs
            .Where(e => e.TaskId == id && e.LockOwner == owner && e.State != CronnerTaskState.Cancelled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.LockedUntilUtc, lockedUntil)
                .SetProperty(e => e.UpdatedUtc, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Owner-conditional in the statement itself: a former owner cannot free a lock someone else holds now.
        var affected = await context.CronnerJobs
            .Where(e => e.TaskId == id && e.LockOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.LockOwner, (string?)null)
                .SetProperty(e => e.LockedUntilUtc, (DateTimeOffset?)null)
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
#pragma warning disable CS0618 // Data is obsolete; still mapped/written while the slot is phased out.
        var affected = await context.CronnerJobExecutions
            .Where(e => e.CorrelationId == execution.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.FinishedAt, execution.FinishedAt)
                .SetProperty(e => e.Status, execution.Status)
                .SetProperty(e => e.Error, execution.Error)
                .SetProperty(e => e.Data, execution.Data), cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore CS0618

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

    /// <inheritdoc />
    public async Task<int> FinalizeOrphanedExecutionsAsync(
        string jobId, DateTimeOffset finishedAt, string error, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // One targeted statement; the (TaskId, StartedAt) index narrows it to this job's rows.
        return await context.CronnerJobExecutions
            .Where(e => e.TaskId == jobId && e.Status == JobExecutionStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.Status, JobExecutionStatus.Failed)
                .SetProperty(e => e.FinishedAt, finishedAt)
                .SetProperty(e => e.Error, error), cancellationToken)
            .ConfigureAwait(false);
    }
}
