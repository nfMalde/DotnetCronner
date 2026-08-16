using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class DiscoveryTests
{
    // Marker used by AutoDiscoverFromType. These attribute jobs live in the TEST assembly, which is never
    // the entry assembly under the test host — so they are only found when explicitly opted in.
    public interface IDiscoverableJob;

    public sealed class MarkedJob : IDiscoverableJob
    {
        [CronnerTask(id: "disc-marked", cronstring: "0 0 * * *")]
        public void Run(CancellationToken ct) { }
    }

    public sealed class UnmarkedJob
    {
        [CronnerTask(id: "disc-unmarked", cronstring: "0 0 * * *")]
        public void Run(CancellationToken ct) { }
    }

    private static async Task<CronnerRegistry> RegistryAfterStartAsync(Action<ICronnerBuilder> configure)
    {
        var host = new HostBuilder()
            .ConfigureServices(services => services.AddDotnetCronner(configure))
            .Build();

        await host.StartAsync();
        try
        {
            return host.Services.GetRequiredService<CronnerRegistry>();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task AutoDiscoverFromAssembly_FindsAttributeTasks()
    {
        var registry = await RegistryAfterStartAsync(c => c
            .DisableAutoDiscovery()
            .AutoDiscoverFromAssembly(typeof(DiscoveryTests)));   // the test assembly

        registry.TryGet("disc-marked", out _).ShouldBeTrue();
        registry.TryGet("disc-unmarked", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task AutoDiscoverFromType_RestrictsToAssignableTypes()
    {
        var registry = await RegistryAfterStartAsync(c => c
            .DisableAutoDiscovery()
            .AutoDiscoverFromAssembly(typeof(DiscoveryTests))
            .AutoDiscoverFromType(typeof(IDiscoverableJob)));

        registry.TryGet("disc-marked", out _).ShouldBeTrue();     // implements the marker
        registry.TryGet("disc-unmarked", out _).ShouldBeFalse();  // filtered out
    }

    [Fact]
    public async Task DisableAutoDiscovery_WithoutOptIn_RegistersNothingFromAttributes()
    {
        var registry = await RegistryAfterStartAsync(c => c.DisableAutoDiscovery());

        registry.TryGet("disc-marked", out _).ShouldBeFalse();
        registry.TryGet("disc-unmarked", out _).ShouldBeFalse();
    }
}
