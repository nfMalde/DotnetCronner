namespace DotnetCronner;

/// <summary>
/// A second-level cache that can sit in front of an <see cref="ICronnerStore"/> to reduce load on
/// the backing store. Implement this to plug in any cache (Redis, in-memory, distributed) or use the
/// built-in Redis provider.
/// </summary>
public interface ICronnerCacheProvider
{
    /// <summary>Returns the cached job for <paramref name="id"/>, or <c>null</c> on a miss.</summary>
    Task<CronnerJob?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="job"/> in the cache under its id.</summary>
    Task SetAsync(CronnerJob job, CancellationToken cancellationToken = default);

    /// <summary>Evicts the job with the given <paramref name="id"/> from the cache.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
