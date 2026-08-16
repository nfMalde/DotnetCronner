using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>Configures the optional second-level cache placed in front of the store.</summary>
public interface ICronnerCacheBuilder
{
    /// <summary>Uses a cache provider resolved from (or created against) the service provider.</summary>
    ICronnerCacheBuilder UseCacheProvider<TProvider>() where TProvider : class, ICronnerCacheProvider;

    /// <summary>Uses a cache provider produced by the given factory. Primarily for built-in providers such as Redis.</summary>
    ICronnerCacheBuilder UseCacheProvider(Func<IServiceProvider, ICronnerCacheProvider> factory);
}

/// <summary>Default implementation that records the chosen cache provider factory.</summary>
internal sealed class CronnerCacheBuilder : ICronnerCacheBuilder
{
    public Func<IServiceProvider, ICronnerCacheProvider>? Factory { get; private set; }

    public ICronnerCacheBuilder UseCacheProvider<TProvider>() where TProvider : class, ICronnerCacheProvider
    {
        Factory = sp => ActivatorUtilities.GetServiceOrCreateInstance<TProvider>(sp);
        return this;
    }

    public ICronnerCacheBuilder UseCacheProvider(Func<IServiceProvider, ICronnerCacheProvider> factory)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }
}
