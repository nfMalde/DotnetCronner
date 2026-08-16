using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class SchedulerExecutionTests
{
    public sealed class Counter
    {
        private int _value;
        public int Value => Volatile.Read(ref _value);
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Increment()
        {
            Interlocked.Increment(ref _value);
            Ran.TrySetResult();
        }
    }

    public sealed class CountingJob(Counter counter)
    {
        public void Run(CancellationToken cancellationToken) => counter.Increment();
    }

    private static IHost BuildHost(Counter counter, Action<ICronnerBuilder> configure)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(counter);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                    });
                    configure(cronner);
                });
            })
            .Build();
    }

    [Fact]
    public async Task CronJob_Executes_OnSchedule()
    {
        var counter = new Counter();
        using var host = BuildHost(counter, cronner => cronner.Sched<CountingJob>(
            x => x.Run(x.HasParam<CancellationToken>()), "* * * * * *")); // every second

        await host.StartAsync();
        try
        {
            var completed = await Task.WhenAny(counter.Ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(counter.Ran.Task);
            counter.Value.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ManualTask_Runs_WhenScheduledViaClient()
    {
        var counter = new Counter();
        using var host = BuildHost(counter, cronner => cronner.Sched<CountingJob>(
            x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("manual"))); // no cron => manual

        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();

            // Not scheduled yet.
            await Task.Delay(300);
            counter.Value.ShouldBe(0);

            await client.ScheduleTaskAsync("manual");

            var completed = await Task.WhenAny(counter.Ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(counter.Ran.Task);
            counter.Value.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task GetTasks_ExposesRegisteredTask()
    {
        var counter = new Counter();
        using var host = BuildHost(counter, cronner => cronner.Sched<CountingJob>(
            x => x.Run(x.HasParam<CancellationToken>()), o => o.WithCron("0 0 * * *").WithId("daily")));

        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var task = await client.GetTaskByIdAsync("daily");
            task.ShouldNotBeNull();
            task!.CronExpression.ShouldBe("0 0 * * *");
            task.NextRunUtc.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
