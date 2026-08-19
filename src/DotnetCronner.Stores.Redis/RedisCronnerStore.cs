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
    private readonly string _execPrefix;

    /// <summary>Creates the store over the given connection with the given key prefix.</summary>
    public RedisCronnerStore(IConnectionMultiplexer redis, string keyPrefix = "cronner:")
    {
        _redis = redis;
        _jobsKey = $"{keyPrefix}jobs";
        _dueKey = $"{keyPrefix}due";
        _lockPrefix = $"{keyPrefix}lock:";
        _execPrefix = $"{keyPrefix}exec:";
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

        // The lock key is the source of truth for claiming and is never touched here; the lock fields inside the
        // stored JSON are informational and, on an update, keep whatever the stored job currently carries so a
        // writer with a stale snapshot cannot make the persisted view lie about a claim taken in the meantime.
        var stored = job;
        var existing = await Db.HashGetAsync(_jobsKey, job.Id).ConfigureAwait(false);
        if (!existing.IsNullOrEmpty && CronnerJobSerializer.Deserialize(existing!) is { } current)
        {
            stored = job.Clone();
            stored.LockOwner = current.LockOwner;
            stored.LockedUntilUtc = current.LockedUntilUtc;
        }

        await Db.HashSetAsync(_jobsKey, job.Id, CronnerJobSerializer.Serialize(stored)).ConfigureAwait(false);

        if (job.NextRunUtc is { } next && job.State != CronnerTaskState.Cancelled)
            await Db.SortedSetAddAsync(_dueKey, job.Id, next.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        else
            await Db.SortedSetRemoveAsync(_dueKey, job.Id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await Db.HashDeleteAsync(_jobsKey, id).ConfigureAwait(false);
        await Db.SortedSetRemoveAsync(_dueKey, id).ConfigureAwait(false);
        await Db.KeyDeleteAsync(LockKey(id)).ConfigureAwait(false);
        // Cascade: a removed job takes its execution history with it.
        await Db.KeyDeleteAsync(ExecKey(id)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0)
            return Array.Empty<CronnerJob>();

        var claimed = new List<CronnerJob>();
        var dueUntil = now.ToUnixTimeMilliseconds();

        // Claimed/running jobs keep their old score in the due set until their run advances the schedule, so
        // the head of the set may be full of members that are locked right now. Page through it (bounded) rather
        // than looking at the first `max` members only, or a second instance with free capacity would see
        // nothing but other instances' running jobs and starve. Within a page the jobs are claimed highest
        // priority first (then earliest due), like the other stores.
        const int maxPages = 8;
        var pageSize = Math.Max(max * 4, 32);
        for (var page = 0; page < maxPages && claimed.Count < max; page++)
        {
            var members = await Db.SortedSetRangeByScoreAsync(
                _dueKey, double.NegativeInfinity, dueUntil,
                Exclude.None, Order.Ascending, skip: page * pageSize, take: pageSize).ConfigureAwait(false);
            if (members.Length == 0)
                break;

            // One round trip for the page's jobs, then order by priority like the relational stores do.
            var values = await Db.HashGetAsync(_jobsKey, members).ConfigureAwait(false);
            var candidates = new List<CronnerJob>(members.Length);
            for (var i = 0; i < members.Length; i++)
            {
                if (values[i].IsNullOrEmpty)
                {
                    // An index entry without a job: the job was removed; drop the stale member.
                    await Db.SortedSetRemoveAsync(_dueKey, members[i]).ConfigureAwait(false);
                    continue;
                }

                if (CronnerJobSerializer.Deserialize(values[i]!) is { } candidate)
                    candidates.Add(candidate);
            }

            foreach (var candidate in candidates
                         .OrderByDescending(j => j.Priority)
                         .ThenBy(j => j.NextRunUtc)
                         .ThenBy(j => j.Id, StringComparer.Ordinal))
            {
                if (claimed.Count >= max)
                    break;

                var id = candidate.Id;
                // Cheap pre-filter on the page snapshot; the claim below re-checks the fresh job anyway.
                if (candidate.State == CronnerTaskState.Cancelled || candidate.NextRunUtc is null || candidate.NextRunUtc > now)
                    continue;

                // Distributed mutual exclusion: only the instance that sets the lock key runs the task. The
                // key carries a server-side TTL, so a crashed owner's claim expires without any clock of ours.
                var gotLock = await Db.StringSetAsync(LockKey(id), owner, lockTtl, When.NotExists).ConfigureAwait(false);
                if (!gotLock)
                    continue;

                // Re-assert eligibility now that we hold the key: the due set can lag behind the job (a cancel or
                // reschedule written a moment ago), and a stale member must not be run.
                var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (job is null || job.State == CronnerTaskState.Cancelled || job.NextRunUtc is null)
                {
                    await Db.SortedSetRemoveAsync(_dueKey, id).ConfigureAwait(false);
                    await Db.KeyDeleteAsync(LockKey(id)).ConfigureAwait(false);
                    continue;
                }

                if (job.NextRunUtc > now)
                {
                    // Rescheduled into the future since the set was indexed; fix the index and let it go.
                    await Db.SortedSetAddAsync(_dueKey, id, job.NextRunUtc.Value.ToUnixTimeMilliseconds()).ConfigureAwait(false);
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

            if (members.Length < pageSize)
                break;
        }

        return claimed;
    }

    // Atomically extends the lock key's TTL only while it still holds this owner — a compare-and-extend in
    // one round-trip so a lock reclaimed by another instance is never re-extended by the old owner.
    private const string RenewLockLua =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    // Deletes the lock key only while it still holds this owner — a compare-and-delete in one round-trip.
    private const string ReleaseLockLua =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    /// <inheritdoc />
    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        // A Cancelled task is not renewed: the engine reads that as "stop this run" (a cancel from another instance).
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null || job.State == CronnerTaskState.Cancelled)
            return false;

        var ttlMs = (long)Math.Max(1, (lockedUntil - DateTimeOffset.UtcNow).TotalMilliseconds);
        var result = await Db.ScriptEvaluateAsync(
            RenewLockLua,
            new RedisKey[] { LockKey(id) },
            new RedisValue[] { owner, ttlMs }).ConfigureAwait(false);

        if ((long)result == 0)
            return false;

        // The lock key is the source of truth for claiming; keep the stored job's expiry roughly in sync
        // so the persisted view (and ICronnerClient) reflects the extended lock.
        if (job.LockOwner == owner)
        {
            job.LockedUntilUtc = lockedUntil;
            await Db.HashSetAsync(_jobsKey, id, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken cancellationToken = default)
    {
        var result = await Db.ScriptEvaluateAsync(
            ReleaseLockLua,
            new RedisKey[] { LockKey(id) },
            new RedisValue[] { owner }).ConfigureAwait(false);

        if ((long)result == 0)
            return false;

        // Informational: clear the lock fields in the stored view too.
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not null && job.LockOwner == owner)
        {
            job.LockOwner = null;
            job.LockedUntilUtc = null;
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

    /// <inheritdoc />
    public async Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        // Each run is a field (keyed by its own correlation id) in a per-job hash — the job link is the key.
        await Db.HashSetAsync(ExecKey(execution.JobId), execution.Id, CronnerJobExecutionSerializer.Serialize(execution))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        // Overwrite the same field (correlation id) with the finalized record; tolerant of a missing start.
        await Db.HashSetAsync(ExecKey(execution.JobId), execution.Id, CronnerJobExecutionSerializer.Serialize(execution))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(
        string jobId, int limit, CancellationToken cancellationToken = default)
    {
        var entries = await Db.HashGetAllAsync(ExecKey(jobId)).ConfigureAwait(false);
        return entries
            .Select(e => CronnerJobExecutionSerializer.Deserialize(e.Value!))
            .Where(e => e is not null)
            .OrderByDescending(e => e!.StartedAt)
            .ThenByDescending(e => e!.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToArray()!;
    }

    /// <inheritdoc />
    public async Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken cancellationToken = default)
    {
        var entries = await Db.HashGetAllAsync(ExecKey(jobId)).ConfigureAwait(false);
        var stale = entries
            .Select(e => CronnerJobExecutionSerializer.Deserialize(e.Value!))
            .Where(e => e is not null)
            .OrderByDescending(e => e!.StartedAt)
            .ThenByDescending(e => e!.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, keepNewest))
            .ToArray();

        if (stale.Length == 0)
            return;

        await Db.HashDeleteAsync(ExecKey(jobId), stale.Select(e => (RedisValue)e!.Id).ToArray()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> FinalizeOrphanedExecutionsAsync(
        string jobId, DateTimeOffset finishedAt, string error, CancellationToken cancellationToken = default)
    {
        var entries = await Db.HashGetAllAsync(ExecKey(jobId)).ConfigureAwait(false);
        var orphaned = entries
            .Select(e => CronnerJobExecutionSerializer.Deserialize(e.Value!))
            .Where(e => e is not null && e.Status == JobExecutionStatus.Running)
            .Select(e => e!)
            .ToArray();

        if (orphaned.Length == 0)
            return 0;

        foreach (var execution in orphaned)
        {
            execution.Status = JobExecutionStatus.Failed;
            execution.FinishedAt = finishedAt;
            execution.Error = error;
        }

        await Db.HashSetAsync(
            ExecKey(jobId),
            orphaned.Select(e => new HashEntry(e.Id, CronnerJobExecutionSerializer.Serialize(e))).ToArray()).ConfigureAwait(false);
        return orphaned.Length;
    }

    private RedisKey LockKey(string id) => $"{_lockPrefix}{id}";

    private RedisKey ExecKey(string jobId) => $"{_execPrefix}{jobId}";
}
