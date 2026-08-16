using System.Collections.Concurrent;

namespace DotnetCronner;

/// <summary>
/// The default zero-configuration store. Keeps all jobs in memory, so schedule and run state is lost
/// when the process restarts and it cannot coordinate multiple instances. Suitable for single-instance
/// apps and tests; use a Redis or EF Core store for durable / distributed scenarios.
/// </summary>
public sealed class InMemoryCronnerStore : ICronnerStore
{
    private readonly ConcurrentDictionary<string, CronnerJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _acquireGate = new();

    /// <inheritdoc />
    public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job.Clone() : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default)
    {
        IEnumerable<CronnerJob> query = _jobs.Values;
        if (state is not null)
            query = query.Where(j => j.State == state);

        var page = query
            .OrderBy(j => j.CreatedUtc)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .Select(j => j.Clone())
            .ToArray();

        return Task.FromResult<IReadOnlyList<CronnerJob>>(page);
    }

    /// <inheritdoc />
    public Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        var copy = job.Clone();
        copy.UpdatedUtc = DateTimeOffset.UtcNow;
        _jobs[copy.Id] = copy;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        _jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        var claimed = new List<CronnerJob>();
        lock (_acquireGate)
        {
            foreach (var job in _jobs.Values
                         .Where(j => IsEligible(j, now))
                         .OrderByDescending(j => j.Priority)
                         .ThenBy(j => j.NextRunUtc)
                         .Take(max))
            {
                job.State = CronnerTaskState.Queued;
                job.LockOwner = owner;
                job.LockedUntilUtc = now + lockTtl;
                job.UpdatedUtc = DateTimeOffset.UtcNow;
                claimed.Add(job.Clone());
            }
        }

        return Task.FromResult<IReadOnlyList<CronnerJob>>(claimed);
    }

    /// <inheritdoc />
    public Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        lock (_acquireGate)
        {
            if (_jobs.TryGetValue(id, out var job) && job.LockOwner == owner)
            {
                job.LockedUntilUtc = lockedUntil;
                job.UpdatedUtc = DateTimeOffset.UtcNow;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private static bool IsEligible(CronnerJob job, DateTimeOffset now) =>
        job.State != CronnerTaskState.Cancelled &&
        job.NextRunUtc is { } next && next <= now &&
        (job.LockOwner is null || job.LockedUntilUtc is null || job.LockedUntilUtc < now);
}
