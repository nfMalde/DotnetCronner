using System.Reflection;
using DotnetCronner.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

/// <summary>
/// Wishlist 0.0.7 §4: a <c>Running</c> history row whose owner died is finalized as <c>Failed</c> (with
/// <see cref="CronnerExecutionErrors.Orphaned"/>) when the task next runs — for non-concurrent tasks, where holding
/// the lock proves the older row is an orphan. Concurrent-mode tasks legitimately overlap and are left alone.
/// </summary>
public class OrphanedExecutionTests
{
    public sealed class QuickJob
    {
        public Task Run() => Task.CompletedTask;
    }

    private static IHost BuildHost(ForwardingStore store, Action<ICronnerScheduleOptions> schedule, TaskCompletionSource done)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ForwardingStore>(store);
                services.AddDotnetCronner(cronner => cronner
                    .UseStore<ForwardingStore>()
                    .Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); })
                    .WithExecutionHistory(10)
                    .OnSuccess(_ => { done.TrySetResult(); return Task.CompletedTask; })
                    .Sched<QuickJob>(x => x.Run(), schedule));
            })
            .Build();
    }

    private static CronnerJobExecution Orphan(string jobId) => new()
    {
        Id = $"orphan-{Guid.NewGuid():N}",
        JobId = jobId,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        Status = JobExecutionStatus.Running,
        Attempt = 1,
        Owner = "instance-that-crashed",
    };

    [Fact]
    public async Task An_Older_Running_Row_Is_Finalized_As_Orphaned_When_The_Task_Next_Runs()
    {
        var store = new ForwardingStore(new InMemoryCronnerStore());
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(store, o => o.WithId("job"), done);

        // A crashed instance left this behind.
        var orphan = Orphan("job");
        await store.RecordExecutionStartedAsync(orphan);

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(done.Task);
            await Task.Delay(300);

            var history = (await store.GetExecutionsAsync("job", 10)).ToDictionary(e => e.Id);
            history.Count.ShouldBe(2);
            var old = history[orphan.Id];
            old.Status.ShouldBe(JobExecutionStatus.Failed);
            old.Error.ShouldBe(CronnerExecutionErrors.Orphaned);
            old.FinishedAt.ShouldNotBeNull();
            history.Values.Single(e => e.Id != orphan.Id).Status.ShouldBe(JobExecutionStatus.Succeeded);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Concurrent_Mode_Task_Leaves_Running_Rows_Alone()
    {
        var store = new ForwardingStore(new InMemoryCronnerStore());
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(store, o => o.WithId("job").WithConcurrency(CronnerConcurrencyMode.Concurrent), done);

        var running = Orphan("job");   // in Concurrent mode this may be a legitimate parallel run, not an orphan
        await store.RecordExecutionStartedAsync(running);

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(done.Task);
            await Task.Delay(300);

            var history = (await store.GetExecutionsAsync("job", 10)).ToDictionary(e => e.Id);
            history[running.Id].Status.ShouldBe(JobExecutionStatus.Running);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void CachedCronnerStore_Implements_Every_Store_Member_Itself()
    {
        // Guard: a default-implemented ICronnerStore member that the cache decorator forgets to forward would run
        // against the decorator (stale cached reads + lock-preserving Upsert) instead of the backing store — e.g.
        // ReleaseLockAsync would silently never release. Every interface member must be declared on the decorator.
        var map = typeof(CachedCronnerStore).GetInterfaceMap(typeof(ICronnerStore));
        var notForwarded = map.TargetMethods
            .Where(m => m.DeclaringType != typeof(CachedCronnerStore))
            .Select(m => m.Name)
            .ToArray();
        notForwarded.ShouldBeEmpty($"CachedCronnerStore must forward: {string.Join(", ", notForwarded)}");
    }
}
