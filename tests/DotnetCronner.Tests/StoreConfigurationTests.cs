using DotnetCronner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DotnetCronner.Tests;

public class StoreConfigurationTests
{
    [Fact]
    public void UseStore_And_UseRedisAsStore_Together_Throws()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<InMemoryCronnerStore>()
            .UseRedisAsStore("localhost:6379")));
    }

    [Fact]
    public void Redis_And_EntityFramework_Together_Throws()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseRedisAsStore("localhost:6379")
            .UseEntityFrameworkStore(o => o.UseInMemoryDatabase("guard-test"))));
    }

    [Fact]
    public void TwoCustomStores_Together_Throws()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<InMemoryCronnerStore>()
            .UseStore<InMemoryCronnerStore>()));
    }

    [Fact]
    public void DefaultStore_IsInMemory()
    {
        var services = new ServiceCollection();
        services.AddDotnetCronner(cronner => cronner.Configure(o => o.ScanEntryAssembly = false));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICronnerStore>().ShouldBeOfType<InMemoryCronnerStore>();
    }

    [Fact]
    public void SecondLevelCache_WrapsStore_InDecorator()
    {
        var services = new ServiceCollection();
        services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseSecondLevelCache(cache => cache.UseCacheProvider<NoopCacheProvider>()));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICronnerStore>().ShouldBeOfType<CachedCronnerStore>();
    }

    private sealed class NoopCacheProvider : ICronnerCacheProvider
    {
        public Task<CronnerJob?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<CronnerJob?>(null);
        public Task SetAsync(CronnerJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }


    /// <summary>
    /// The first UseStore replaces the default in-memory store; a second call is rejected and names
    /// the store already in effect, not the one being added -- when someone hits this, the useful
    /// information is what they are already using.
    /// </summary>
    [Fact]
    public void Second_Store_Throws_And_Names_The_Active_One()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(() => services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<ScopedStore>()
            .UseRedisAsStore("localhost:6379")));

        ex.Message.ShouldContain(nameof(ScopedStore));          // the active store
        ex.Message.ShouldContain("only use one store");
        ex.Message.ShouldContain("UseRedisAsStore()");          // the rejected call
    }

    /// <summary>A single UseStore is of course fine — the guard only trips on the second.</summary>
    [Fact]
    public void One_Store_Is_Allowed()
    {
        var services = new ServiceCollection();

        Should.NotThrow(() => services.AddDotnetCronner(cronner => cronner
            .Configure(o => o.ScanEntryAssembly = false)
            .UseStore<ScopedStore>()));
    }

    /// <summary>A store whose only relevant property is that a test can register it scoped.</summary>
    private sealed class ScopedStore : ICronnerStore
    {
        private readonly InMemoryCronnerStore _inner = new();

        public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default)
            => _inner.GetAsync(state, offset, limit, ct);
        public Task UpsertAsync(CronnerJob job, CancellationToken ct = default) => _inner.UpsertAsync(job, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => _inner.RemoveAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default)
            => _inner.AcquireDueAsync(now, owner, lockTtl, max, ct);
        public Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default)
            => _inner.RenewLockAsync(id, owner, lockedUntil, ct);
    }
}
