using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DotnetCronner;

/// <summary>Resolves an <see cref="IConnectionMultiplexer"/> from the configured options or DI.</summary>
internal static class RedisConnectionResolver
{
    public static IConnectionMultiplexer Resolve(IServiceProvider services, CronnerRedisOptions options)
    {
        if (options.ConnectionMultiplexerFactory is not null)
            return options.ConnectionMultiplexerFactory(services);

        if (services.GetService<IConnectionMultiplexer>() is { } registered)
            return registered;

        if (options.ConfigurationOptions is not null)
            return ConnectionMultiplexer.Connect(options.ConfigurationOptions);

        if (!string.IsNullOrWhiteSpace(options.Configuration))
            return ConnectionMultiplexer.Connect(options.Configuration);

        throw new InvalidOperationException(
            "Redis is not configured. Set CronnerRedisOptions.Configuration, ConfigurationOptions, or " +
            "ConnectionMultiplexerFactory, or register an IConnectionMultiplexer in DI.");
    }
}
