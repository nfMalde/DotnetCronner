namespace DotnetCronner;

/// <summary>Fluent extensions for using Redis as the DotnetCronner store or second-level cache.</summary>
public static class CronnerRedisBuilderExtensions
{
    /// <summary>Uses Redis as the backing store. Mutually exclusive with other store providers.</summary>
    public static ICronnerBuilder UseRedisAsStore(this ICronnerBuilder builder, Action<CronnerRedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CronnerRedisOptions();
        configure(options);
        builder.StoreHolder.ConfigureStore(
            sp => new RedisCronnerStore(RedisConnectionResolver.Resolve(sp, options), options.KeyPrefix),
            "UseRedisAsStore()");
        return builder;
    }

    /// <summary>
    /// Uses Redis as the backing store, configuring it from DI. The callback runs when the store is built
    /// and receives the application's <see cref="IServiceProvider"/>, so you can pull the connection (and
    /// credentials) from <c>IConfiguration</c>, options, or any registered service.
    /// </summary>
    public static ICronnerBuilder UseRedisAsStore(this ICronnerBuilder builder, Action<IServiceProvider, CronnerRedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.StoreHolder.ConfigureStore(
            sp =>
            {
                var options = new CronnerRedisOptions();
                configure(sp, options);
                return new RedisCronnerStore(RedisConnectionResolver.Resolve(sp, options), options.KeyPrefix);
            },
            "UseRedisAsStore()");
        return builder;
    }

    /// <summary>Uses Redis as the backing store, connecting with the given configuration string.</summary>
    public static ICronnerBuilder UseRedisAsStore(this ICronnerBuilder builder, string configuration) =>
        builder.UseRedisAsStore(options => options.Configuration = configuration);

    /// <summary>
    /// Uses Redis as the second-level cache provider. Cache keys are written under
    /// <c>{KeyPrefix}cache:</c> — the <c>cache:</c> segment is added automatically, so leave it out of
    /// <see cref="CronnerRedisOptions.KeyPrefix"/> to avoid doubled keys.
    /// </summary>
    public static ICronnerCacheBuilder UseRedisCacheProvider(this ICronnerCacheBuilder cache, Action<CronnerRedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CronnerRedisOptions();
        configure(options);
        return cache.UseCacheProvider(
            sp => new RedisCronnerCacheProvider(RedisConnectionResolver.Resolve(sp, options), options.KeyPrefix, options.CacheTtl));
    }

    /// <summary>
    /// Uses Redis as the second-level cache provider, configuring it from DI. The callback runs when the
    /// provider is built and receives the application's <see cref="IServiceProvider"/>, so the connection
    /// and credentials can come from <c>IConfiguration</c> or any registered service.
    /// </summary>
    public static ICronnerCacheBuilder UseRedisCacheProvider(this ICronnerCacheBuilder cache, Action<IServiceProvider, CronnerRedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(configure);

        return cache.UseCacheProvider(sp =>
        {
            var options = new CronnerRedisOptions();
            configure(sp, options);
            return new RedisCronnerCacheProvider(RedisConnectionResolver.Resolve(sp, options), options.KeyPrefix, options.CacheTtl);
        });
    }

    /// <summary>Uses Redis as the second-level cache provider, connecting with the given configuration string.</summary>
    public static ICronnerCacheBuilder UseRedisCacheProvider(this ICronnerCacheBuilder cache, string configuration) =>
        cache.UseRedisCacheProvider(options => options.Configuration = configuration);
}
