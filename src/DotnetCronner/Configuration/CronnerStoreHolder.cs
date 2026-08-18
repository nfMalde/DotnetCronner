using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// Holds the chosen store and optional cache factories so the actual <see cref="ICronnerStore"/> can
/// be assembled lazily. This lets configuration happen either at <c>AddDotnetCronner</c> time or at
/// <c>app.UseDotnetCronner</c> time (after the DI container has been built) while the engine keeps a
/// single stable <see cref="ICronnerStore"/> registration.
///
/// <para><b>Why the lifetime lives here and not in DI.</b> The store is consumed by the scheduler,
/// which is a singleton, and configuration may arrive after the container is built — at which point
/// no registration can take effect. Expressing the lifetime on the holder keeps both paths identical
/// and keeps the store out of <c>WithDedicatedDI</c>'s isolated job container, where its own
/// dependencies would not be resolvable.</para>
/// </summary>
public sealed class CronnerStoreHolder
{
    private readonly object _gate = new();
    private Func<IServiceProvider, ICronnerStore> _storeFactory = _ => new InMemoryCronnerStore();
    private CronnerStoreLifetime _lifetime = CronnerStoreLifetime.Singleton;
    private Func<IServiceProvider, ICronnerCacheProvider>? _cacheFactory;
    private bool _storeConfigured;
    private string? _activeStore;
    private ICronnerStore? _singleton;
    private ICronnerCacheProvider? _cache;

    /// <summary>
    /// Sets the backing store factory and how often it is built.
    ///
    /// <para>The first call replaces the default in-memory store. A second call throws: the scheduler
    /// has exactly one source of truth, and silently letting the last registration win would make the
    /// effective store depend on call order — the kind of thing that only shows up when jobs run
    /// against a store nobody expected.</para>
    /// </summary>
    /// <param name="factory">Builds the store from the service provider.</param>
    /// <param name="source">A short label of the calling API, reported when a second store is added.</param>
    /// <param name="lifetime">
    ///     <see cref="CronnerStoreLifetime.Singleton"/> for a stateless or self-synchronising store;
    ///     <see cref="CronnerStoreLifetime.Scoped"/> for one holding a DbContext, ORM session or
    ///     connection.
    /// </param>
    public void ConfigureStore(
        Func<IServiceProvider, ICronnerStore> factory,
        string source,
        CronnerStoreLifetime lifetime = CronnerStoreLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_storeConfigured)
                throw new InvalidOperationException(
                    $"A custom store {_activeStore} is already used. You can only use one store. " +
                    $"'{source}' would replace it.");

            _storeFactory = factory;
            _lifetime = lifetime;
            _storeConfigured = true;
            _activeStore = source;
        }
    }

    /// <summary>Sets the optional second-level cache factory.</summary>
    public void ConfigureCache(Func<IServiceProvider, ICronnerCacheProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _cacheFactory = factory;
    }

    /// <summary>The configured lifetime. Exposed so the engine knows whether a per-operation scope is needed.</summary>
    public CronnerStoreLifetime Lifetime => _lifetime;

    /// <summary>
    /// Returns the effective store for one operation, wrapped in the cache decorator when configured.
    ///
    /// <para>Under <see cref="CronnerStoreLifetime.Singleton"/> the instance is built once and reused
    /// — <paramref name="services"/> is only consulted on that first call. Under
    /// <see cref="CronnerStoreLifetime.Scoped"/> the factory runs every time, so
    /// <paramref name="services"/> MUST be the operation's scoped provider; passing the root provider
    /// would rebuild the store while still resolving its dependencies from root, which is the captive
    /// dependency the scoped lifetime exists to avoid.</para>
    /// </summary>
    public ICronnerStore Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_lifetime == CronnerStoreLifetime.Scoped)
            return Decorate(_storeFactory(services), services);

        if (_singleton is not null)
            return _singleton;

        lock (_gate)
        {
            if (_singleton is not null)
                return _singleton;

            _singleton = Decorate(_storeFactory(services), services);
            return _singleton;
        }
    }

    /// <summary>
    /// Obsolete alias for <see cref="Resolve"/>. Kept so existing callers keep compiling; it always
    /// behaved as a singleton, which is still what <see cref="CronnerStoreLifetime.Singleton"/> does.
    /// </summary>
    [Obsolete("Use Resolve(IServiceProvider). Build() cannot honour a scoped store lifetime.")]
    public ICronnerStore Build(IServiceProvider services) => Resolve(services);

    /// <summary>
    /// Wraps the store in the second-level cache when one is configured.
    ///
    /// <para>The cache PROVIDER is built once even for a scoped store: it is infrastructure (typically
    /// a connection multiplexer), and rebuilding it per operation would defeat the point of caching
    /// and churn connections. Only the thin decorator is per-operation.</para>
    /// </summary>
    private ICronnerStore Decorate(ICronnerStore store, IServiceProvider services)
    {
        if (_cacheFactory is null)
            return store;

        _cache ??= _cacheFactory(services);
        return new CachedCronnerStore(store, _cache);
    }
}
