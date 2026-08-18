using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DotnetCronner.Tests;

/// <summary>
/// Covers <see cref="CronnerStoreLifetime"/> — how often the configured store is built.
///
/// <para>This exists because the previous behaviour (build once, from the root provider, always) made
/// a store holding a DbContext or ORM session share one instance across every concurrent scheduler
/// operation. That surfaces as "a command is already in progress" or "a second operation was started
/// on this context instance", and only once enough tasks are registered for the seeding fan-out to
/// overlap — so it hides in small samples and shows up in production.</para>
/// </summary>
public class StoreLifetimeTests
{
    // ---- Singleton -----------------------------------------------------------------------------

    [Fact]
    public void Singleton_Is_The_Default()
    {
        new CronnerStoreHolder().Lifetime.ShouldBe(CronnerStoreLifetime.Singleton);
    }

    [Fact]
    public void Singleton_Builds_The_Store_Exactly_Once()
    {
        var built = 0;
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(_ => { built++; return new CountingStore(); }, "test", CronnerStoreLifetime.Singleton);

        using var provider = new ServiceCollection().BuildServiceProvider();

        var first = holder.Resolve(provider);
        var second = holder.Resolve(provider);

        built.ShouldBe(1);
        second.ShouldBeSameAs(first);
    }

    /// <summary>
    /// The in-memory default IS its own state. Rebuilding it per resolve would silently discard every
    /// job — data loss with no exception — so it gets an explicit test rather than relying on the
    /// default lifetime happening to stay Singleton.
    /// </summary>
    [Fact]
    public async Task Default_InMemory_Store_Keeps_Its_State_Across_Resolves()
    {
        var holder = new CronnerStoreHolder();
        using var provider = new ServiceCollection().BuildServiceProvider();

        await holder.Resolve(provider).UpsertAsync(new CronnerJob { Id = "job-a", Name = "A" });

        (await holder.Resolve(provider).GetByIdAsync("job-a")).ShouldNotBeNull();
    }

    // ---- Scoped --------------------------------------------------------------------------------

    [Fact]
    public void Scoped_Builds_The_Store_On_Every_Resolve()
    {
        var built = 0;
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(_ => { built++; return new CountingStore(); }, "test", CronnerStoreLifetime.Scoped);

        using var provider = new ServiceCollection().BuildServiceProvider();

        var first = holder.Resolve(provider);
        var second = holder.Resolve(provider);

        built.ShouldBe(2);
        second.ShouldNotBeSameAs(first);
    }

    /// <summary>
    /// The whole point of the scoped lifetime: the factory receives the operation's provider, so a
    /// store depending on a scoped service gets that scope's instance rather than a captured one.
    /// </summary>
    [Fact]
    public void Scoped_Store_Receives_The_Scopes_Own_Dependencies()
    {
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(
            sp => new DependentStore(sp.GetRequiredService<ScopedDependency>()),
            "test",
            CronnerStoreLifetime.Scoped);

        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        using var provider = services.BuildServiceProvider();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var a = (DependentStore)holder.Resolve(scopeA.ServiceProvider);
        var b = (DependentStore)holder.Resolve(scopeB.ServiceProvider);

        a.Dependency.ShouldNotBeSameAs(b.Dependency);
    }

    /// <summary>Two resolves in the SAME scope share that scope's dependency, as DI intends.</summary>
    [Fact]
    public void Scoped_Store_Shares_Dependencies_Within_One_Scope()
    {
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(
            sp => new DependentStore(sp.GetRequiredService<ScopedDependency>()),
            "test",
            CronnerStoreLifetime.Scoped);

        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var a = (DependentStore)holder.Resolve(scope.ServiceProvider);
        var b = (DependentStore)holder.Resolve(scope.ServiceProvider);

        a.ShouldNotBeSameAs(b);                       // a new store...
        a.Dependency.ShouldBeSameAs(b.Dependency);    // ...over the same scoped dependency
    }

    // ---- Accessor ------------------------------------------------------------------------------

    /// <summary>
    /// The accessor is what carries the scoped lifetime into the scheduler: it opens a scope per
    /// operation, so the singleton hosted service never captures a store.
    /// </summary>
    [Fact]
    public async Task Accessor_Gives_A_Scoped_Store_A_Fresh_Instance_Per_Operation()
    {
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(
            sp => new DependentStore(sp.GetRequiredService<ScopedDependency>()),
            "test",
            CronnerStoreLifetime.Scoped);

        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddSingleton(holder);
        using var provider = services.BuildServiceProvider();

        var accessor = new CronnerStoreAccessor(provider.GetRequiredService<IServiceScopeFactory>(), holder);

        ScopedDependency? first = null;
        ScopedDependency? second = null;
        await accessor.UseAsync(s => { first = ((DependentStore)s).Dependency; return Task.CompletedTask; });
        await accessor.UseAsync(s => { second = ((DependentStore)s).Dependency; return Task.CompletedTask; });

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);
    }

    /// <summary>A singleton store still resolves to one instance through the accessor.</summary>
    [Fact]
    public async Task Accessor_Gives_A_Singleton_Store_The_Same_Instance()
    {
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(_ => new CountingStore(), "test", CronnerStoreLifetime.Singleton);

        var services = new ServiceCollection();
        services.AddSingleton(holder);
        using var provider = services.BuildServiceProvider();

        var accessor = new CronnerStoreAccessor(provider.GetRequiredService<IServiceScopeFactory>(), holder);

        ICronnerStore? first = null;
        ICronnerStore? second = null;
        await accessor.UseAsync(s => { first = s; return Task.CompletedTask; });
        await accessor.UseAsync(s => { second = s; return Task.CompletedTask; });

        second.ShouldBeSameAs(first);
    }

    /// <summary>
    /// The regression this whole change is about: concurrent operations must never be handed the same
    /// scoped store, because that is one DbContext or ORM session serving overlapping commands.
    /// </summary>
    [Fact]
    public async Task Concurrent_Operations_Do_Not_Share_A_Scoped_Store()
    {
        var holder = new CronnerStoreHolder();
        holder.ConfigureStore(_ => new CountingStore(), "test", CronnerStoreLifetime.Scoped);

        var services = new ServiceCollection();
        services.AddSingleton(holder);
        using var provider = services.BuildServiceProvider();

        var accessor = new CronnerStoreAccessor(provider.GetRequiredService<IServiceScopeFactory>(), holder);

        var seen = new ConcurrentBag<ICronnerStore>();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            accessor.UseAsync(s => { seen.Add(s); return Task.CompletedTask; })));

        seen.Distinct().Count().ShouldBe(20);
    }

    // ---- Registration wiring -------------------------------------------------------------------

    /// <summary>The DI registration follows the ambient scope, so an injected store honours the lifetime.</summary>
    [Fact]
    public void Registered_Store_Follows_The_Scope_When_Scoped()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<DependentStore>(CronnerStoreLifetime.Scoped));

        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        scopeA.ServiceProvider.GetRequiredService<ICronnerStore>()
            .ShouldNotBeSameAs(scopeB.ServiceProvider.GetRequiredService<ICronnerStore>());
    }

    [Fact]
    public void Registered_Store_Is_Shared_When_Singleton()
    {
        var services = new ServiceCollection();
        services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<CountingStore>());

        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        scopeA.ServiceProvider.GetRequiredService<ICronnerStore>()
            .ShouldBeSameAs(scopeB.ServiceProvider.GetRequiredService<ICronnerStore>());
    }

    // ---- Test doubles --------------------------------------------------------------------------

    private sealed class ScopedDependency;

    private class CountingStore : ICronnerStore
    {
        private readonly InMemoryCronnerStore _inner = new();

        public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<CronnerJob>> GetAsync(
            CronnerTaskState? state, int offset, int limit, CancellationToken ct = default)
            => _inner.GetAsync(state, offset, limit, ct);

        public Task UpsertAsync(CronnerJob job, CancellationToken ct = default) => _inner.UpsertAsync(job, ct);

        public Task RemoveAsync(string id, CancellationToken ct = default) => _inner.RemoveAsync(id, ct);

        public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
            DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default)
            => _inner.AcquireDueAsync(now, owner, lockTtl, max, ct);

        public Task<bool> RenewLockAsync(
            string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default)
            => _inner.RenewLockAsync(id, owner, lockedUntil, ct);
    }

    private sealed class DependentStore(ScopedDependency dependency) : CountingStore
    {
        public ScopedDependency Dependency { get; } = dependency;
    }
}
