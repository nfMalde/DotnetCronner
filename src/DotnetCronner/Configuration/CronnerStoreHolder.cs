using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// Holds the chosen store and optional cache factories so the actual <see cref="ICronnerStore"/> can
/// be assembled lazily. This lets configuration happen either at <c>AddDotnetCronner</c> time or at
/// <c>app.UseDotnetCronner</c> time (after the DI container has been built) while the engine keeps a
/// single stable <see cref="ICronnerStore"/> registration.
/// </summary>
public sealed class CronnerStoreHolder
{
    private readonly object _gate = new();
    private Func<IServiceProvider, ICronnerStore> _storeFactory = _ => new InMemoryCronnerStore();
    private Func<IServiceProvider, ICronnerCacheProvider>? _cacheFactory;
    private bool _storeConfigured;
    private ICronnerStore? _built;

    /// <summary>Sets the backing store factory. Throws if a non-default store was already configured.</summary>
    /// <param name="factory">Builds the store from the service provider.</param>
    /// <param name="source">A short label of the calling API, used in the conflict error message.</param>
    public void ConfigureStore(Func<IServiceProvider, ICronnerStore> factory, string source)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_storeConfigured)
                throw new InvalidOperationException(
                    $"A DotnetCronner store is already configured; '{source}' cannot be combined with another " +
                    "UseStore/UseRedisAsStore/UseEntityFrameworkStore call.");

            _storeFactory = factory;
            _storeConfigured = true;
        }
    }

    /// <summary>Sets the optional second-level cache factory.</summary>
    public void ConfigureCache(Func<IServiceProvider, ICronnerCacheProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _cacheFactory = factory;
    }

    /// <summary>Builds (once) the effective store, wrapping it in the cache decorator when configured.</summary>
    public ICronnerStore Build(IServiceProvider services)
    {
        if (_built is not null)
            return _built;

        lock (_gate)
        {
            if (_built is not null)
                return _built;

            var store = _storeFactory(services);
            if (_cacheFactory is not null)
                store = new CachedCronnerStore(store, _cacheFactory(services));

            _built = store;
            return _built;
        }
    }
}
