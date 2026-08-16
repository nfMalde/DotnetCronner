using StackExchange.Redis;

namespace DotnetCronner;

/// <summary>Configuration for the Redis store and Redis second-level cache.</summary>
public sealed class CronnerRedisOptions
{
    /// <summary>A StackExchange.Redis connection string, e.g. <c>localhost:6379</c>.</summary>
    public string? Configuration { get; set; }

    /// <summary>Fully specified connection options (takes precedence over <see cref="Configuration"/>).</summary>
    public ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>A factory for the connection multiplexer, e.g. to reuse an existing one.</summary>
    public Func<IServiceProvider, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// Prefix applied to all keys written by DotnetCronner. Defaults to <c>cronner:</c>. The store appends
    /// its own segments (<c>jobs</c>, <c>due</c>, <c>lock:</c>) and the cache provider appends <c>cache:</c>,
    /// so do <b>not</b> include a <c>cache:</c> segment here — a value like <c>cronner:cache:</c> would
    /// produce doubled keys such as <c>cronner:cache:cache:&lt;id&gt;</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "cronner:";

    /// <summary>Optional expiry for entries written by the Redis cache provider. <c>null</c> means no expiry.</summary>
    public TimeSpan? CacheTtl { get; set; }
}
