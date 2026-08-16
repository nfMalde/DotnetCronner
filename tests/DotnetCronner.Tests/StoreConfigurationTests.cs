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
}
