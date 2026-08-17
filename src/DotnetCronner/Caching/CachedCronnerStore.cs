namespace DotnetCronner;

/// <summary>
/// A read-through / write-through decorator that puts an <see cref="ICronnerCacheProvider"/> in front
/// of a backing <see cref="ICronnerStore"/> as a second-level cache.
/// </summary>
public sealed class CachedCronnerStore : ICronnerStore
{
    private readonly ICronnerStore _inner;
    private readonly ICronnerCacheProvider _cache;

    /// <summary>Creates the decorator around <paramref name="inner"/> using <paramref name="cache"/>.</summary>
    public CachedCronnerStore(ICronnerStore inner, ICronnerCacheProvider cache)
    {
        _inner = inner;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var job = await _inner.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not null)
            await _cache.SetAsync(job, cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default) =>
        // Lists are not cached; page queries always hit the backing store.
        _inner.GetAsync(state, offset, limit, cancellationToken);

    /// <inheritdoc />
    public async Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        await _cache.SetAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _inner.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        await _cache.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        var claimed = await _inner.AcquireDueAsync(now, owner, lockTtl, max, cancellationToken).ConfigureAwait(false);
        foreach (var job in claimed)
            await _cache.SetAsync(job, cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    /// <inheritdoc />
    public Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default) =>
        // Lock renewal is a backing-store concern; the cache holds no authoritative lock state.
        _inner.RenewLockAsync(id, owner, lockedUntil, cancellationToken);

    /// <inheritdoc />
    public Task OnStartAsync(CronnerJob job, CancellationToken cancellationToken = default) =>
        _inner.OnStartAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task OnCloseAsync(CronnerJob job, CancellationToken cancellationToken = default) =>
        _inner.OnCloseAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken cancellationToken = default) =>
        _inner.PruneCompletedOneOffsAsync(definitionId, keepNewest, cancellationToken);

    /// <inheritdoc />
    public async Task UpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken = default)
    {
        await _inner.UpdateProgressAsync(id, progress, cancellationToken).ConfigureAwait(false);
        var job = await _inner.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not null)
            await _cache.SetAsync(job, cancellationToken).ConfigureAwait(false);
    }
}
