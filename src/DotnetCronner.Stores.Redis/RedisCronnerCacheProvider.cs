using StackExchange.Redis;

namespace DotnetCronner;

/// <summary>A second-level cache provider backed by Redis string keys.</summary>
public sealed class RedisCronnerCacheProvider : ICronnerCacheProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _cachePrefix;
    private readonly TimeSpan? _ttl;

    /// <summary>Creates the provider over the given connection.</summary>
    public RedisCronnerCacheProvider(IConnectionMultiplexer redis, string keyPrefix = "cronner:", TimeSpan? ttl = null)
    {
        _redis = redis;
        _cachePrefix = $"{keyPrefix}cache:";
        _ttl = ttl;
    }

    private IDatabase Db => _redis.GetDatabase();

    /// <inheritdoc />
    public async Task<CronnerJob?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var value = await Db.StringGetAsync(CacheKey(id)).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : CronnerJobSerializer.Deserialize(value!);
    }

    /// <inheritdoc />
    public async Task SetAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(job.Id);
        await Db.StringSetAsync(key, CronnerJobSerializer.Serialize(job)).ConfigureAwait(false);
        if (_ttl is { } ttl)
            await Db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        Db.KeyDeleteAsync(CacheKey(id));

    private RedisKey CacheKey(string id) => $"{_cachePrefix}{id}";
}
