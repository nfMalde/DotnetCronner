using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class DedicatedServicesTests
{
    public sealed class Dependency
    {
        public int Value { get; set; } = 41;
    }

    public sealed class Tracker
    {
        public int Instances;
        public int Runs;
        public int LastValue;
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoRuns { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class DependencyJob(Tracker tracker)
    {
        public void Run(Dependency dependency)
        {
            tracker.LastValue = dependency.Value;
            tracker.Ran.TrySetResult();
        }
    }

    public sealed class LifetimeJob
    {
        private readonly Tracker _tracker;

        public LifetimeJob(Tracker tracker)
        {
            _tracker = tracker;
            Interlocked.Increment(ref tracker.Instances);
        }

        public void Run()
        {
            if (Interlocked.Increment(ref _tracker.Runs) >= 2)
                _tracker.TwoRuns.TrySetResult();
        }
    }

    private static IHost BuildHost(Action<ICronnerBuilder> configure)
    {
        return new HostBuilder()
            .ConfigureServices(services => services.AddDotnetCronner(cronner =>
            {
                cronner.Configure(o =>
                {
                    o.ScanEntryAssembly = false;
                    o.PollingInterval = TimeSpan.FromMilliseconds(50);
                });
                configure(cronner);
            }))
            .Build();
    }

    [Fact]
    public async Task Job_Resolves_FromDedicatedProvider()
    {
        // Dependency is registered ONLY in the dedicated collection, never in the application provider.
        var tracker = new Tracker();
        var dependency = new Dependency { Value = 99 };

        using var host = BuildHost(cronner => cronner
            .WithDedicatedDI(services =>
            {
                services.AddSingleton(tracker);
                services.AddSingleton(dependency);
            })
            .Sched<DependencyJob>(x => x.Run(x.HasParam<Dependency>()), o => o.WithId("dep")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("dep");

            var completed = await Task.WhenAny(tracker.Ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(tracker.Ran.Task);
            tracker.LastValue.ShouldBe(99);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SingletonJobLifetime_ReusesSingleInstance()
    {
        var tracker = new Tracker();

        using var host = BuildHost(cronner => cronner
            .WithDedicatedDI(services => services.AddSingleton(tracker), ServiceLifetime.Singleton)
            .Sched<LifetimeJob>(x => x.Run(), "* * * * * *")); // every second

        await host.StartAsync();
        try
        {
            var completed = await Task.WhenAny(tracker.TwoRuns.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            completed.ShouldBeSameAs(tracker.TwoRuns.Task);

            tracker.Runs.ShouldBeGreaterThanOrEqualTo(2);
            tracker.Instances.ShouldBe(1); // one job instance reused across runs
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
