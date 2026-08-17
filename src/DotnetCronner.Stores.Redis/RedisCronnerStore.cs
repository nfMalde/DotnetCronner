using StackExchange.Redis;

namespace DotnetCronner;

/// <summary>
/// A distributed <see cref="ICronnerStore"/> backed by Redis. Jobs are stored as JSON in a hash, a
/// sorted set indexes them by next-run time, and per-job lock keys with a TTL provide distributed,
/// crash-safe claiming so multiple app instances never run the same task at once.
/// </summary>
public sealed class RedisCronnerStore : ICronnerStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _jobsKey;
    private readonly string _dueKey;
    private readonly string _lockPrefix;

    /// <summary>Creates the store over the given connection with the given key prefix.</summary>
    public RedisCronnerStore(IConnectionMultiplexer redis, string keyPrefix = "cronner:")
    {
        _redis = redis;
        _jobsKey = $"{keyPrefix}jobs";
        _dueKey = $"{keyPrefix}due";
        _lockPrefix = $"{keyPrefix}lock:";
    }

    private IDatabase Db => _redis.GetDatabase();

    /// <inheritdoc />
    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var value = await Db.HashGetAsync(_jobsKey, id).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : CronnerJobSerializer.Deserialize(value!);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var entries = await Db.HashGetAllAsync(_jobsKey).ConfigureAwait(false);
        IEnumerable<CronnerJob> jobs = entries
            .Select(e => CronnerJobSerializer.Deserialize(e.Value!))
            .Where(j => j is not null)!;

        if (state is not null)
            jobs = jobs.Where(j => j!.State == state);

        return jobs
            .OrderBy(j => j!.CreatedUtc)
            .ThenBy(j => j!.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToArray()!;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await Db.HashSetAsync(_jobsKey, job.Id, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);

        if (job.NextRunUtc is { } next && job.State != CronnerTaskState.Cancelled)
            await Db.SortedSetAddAsync(_dueKey, job.Id, next.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        else
            await Db.SortedSetRemoveAsync(_dueKey, job.Id).ConfigureAwait(false);

        // Releasing the lock (no owner) frees the task for the next claim.
        if (job.LockOwner is null)
            await Db.KeyDeleteAsync(LockKey(job.Id)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await Db.HashDeleteAsync(_jobsKey, id).ConfigureAwait(false);
        await Db.SortedSetRemoveAsync(_dueKey, id).ConfigureAwait(false);
        await Db.KeyDeleteAsync(LockKey(id)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        var candidates = await Db.SortedSetRangeByScoreAsync(
            _dueKey, double.NegativeInfinity, now.ToUnixTimeMilliseconds(),
            Exclude.None, Order.Ascending, skip: 0, take: max).ConfigureAwait(false);

        var claimed = new List<CronnerJob>();
        foreach (var member in candidates)
        {
            if (claimed.Count >= max)
                break;

            var id = member.ToString();

            // Distributed mutual exclusion: only the instance that sets the lock key runs the task.
            var gotLock = await Db.StringSetAsync(LockKey(id), owner, lockTtl, When.NotExists).ConfigureAwait(false);
            if (!gotLock)
                continue;

            var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                await Db.SortedSetRemoveAsync(_dueKey, id).ConfigureAwait(false);
                await Db.KeyDeleteAsync(LockKey(id)).ConfigureAwait(false);
                continue;
            }

            job.State = CronnerTaskState.Queued;
            job.LockOwner = owner;
            job.LockedUntilUtc = now + lockTtl;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            await Db.HashSetAsync(_jobsKey, id, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);
            claimed.Add(job);
        }

        return claimed;
    }

    // Atomically extends the lock key's TTL only while it still holds this owner — a compare-and-extend in
    // one round-trip so a lock reclaimed by another instance is never re-extended by the old owner.
    private const string RenewLockLua =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    /// <inheritdoc />
    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        var ttlMs = (long)Math.Max(1, (lockedUntil - DateTimeOffset.UtcNow).TotalMilliseconds);
        var result = await Db.ScriptEvaluateAsync(
            RenewLockLua,
            new RedisKey[] { LockKey(id) },
            new RedisValue[] { owner, ttlMs }).ConfigureAwait(false);

        if ((long)result == 0)
            return false;

        // The lock key is the source of truth for claiming; keep the stored job's expiry roughly in sync
        // so the persisted view (and ICronnerClient) reflects the extended lock.
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not null && job.LockOwner == owner)
        {
            job.LockedUntilUtc = lockedUntil;
            await Db.HashSetAsync(_jobsKey, id, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken cancellationToken = default)
    {
        var entries = await Db.HashGetAllAsync(_jobsKey).ConfigureAwait(false);
        var stale = entries
            .Select(e => CronnerJobSerializer.Deserialize(e.Value!))
            .Where(j => j is not null && j.Kind == CronnerJobKind.OneOff && j.DefinitionId == definitionId &&
                        (j.State == CronnerTaskState.Completed || j.State == CronnerTaskState.Failed))
            .OrderByDescending(j => j!.UpdatedUtc)
            .Skip(Math.Max(0, keepNewest))
            .ToArray();

        foreach (var job in stale)
            await RemoveAsync(job!.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken = default)
    {
        // Read-modify-write keeps the current lock/schedule fields (incl. any keepalive renewal) intact.
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return;

        job.Progress = progress;
        await Db.HashSetAsync(_jobsKey, id, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);
    }

    private RedisKey LockKey(string id) => $"{_lockPrefix}{id}";
}
