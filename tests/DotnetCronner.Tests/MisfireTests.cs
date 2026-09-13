using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class MisfireTests
{
    public sealed class Counter { public int Runs; }

    public sealed class TickJob(Counter counter)
    {
        public void Run(CancellationToken ct) => Interlocked.Increment(ref counter.Runs);
    }

    // Hourly cron with a NextRunUtc several hours in the past: the missed window contains an exact number of
    // occurrences (one per hour) and the NEXT future occurrence is up to an hour away, so no normal tick fires
    // during the short test — the run count reflects only the misfire policy.
    private static IHost Build(Counter counter, InMemoryCronnerStore store, MisfirePolicy policy, Action<CronnerOptions>? extra = null) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(counter);
                services.AddSingleton(store);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                        o.MisfireThreshold = TimeSpan.FromMilliseconds(500);
                        extra?.Invoke(o);
                    });
                    cronner.UseStore<InMemoryCronnerStore>();
                    cronner.Sched<TickJob>(
                        x => x.Run(x.HasParam<CancellationToken>()),
                        o => o.WithCron("0 * * * *").WithId("tick").WithMisfirePolicy(policy));
                });
            })
            .Build();

    private static async Task<InMemoryCronnerStore> StaleStoreAsync(TimeSpan staleBy)
    {
        var store = new InMemoryCronnerStore();
        await store.UpsertAsync(new CronnerJob
        {
            Id = "tick",
            Name = "tick",
            CronExpression = "0 * * * *",
            NextRunUtc = DateTimeOffset.UtcNow - staleBy,
            State = CronnerTaskState.Scheduled,
        });
        return store;
    }

    // Waits until the misfire has been resolved (NextRunUtc has moved to a future occurrence), then returns the
    // run count — which is stable because the next occurrence is far off.
    private static async Task<int> SettleAsync(IHost host, Counter counter)
    {
        var client = host.Services.GetRequiredService<ICronnerClient>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var job = await client.GetTaskByIdAsync("tick");
            if (job?.NextRunUtc is { } next && next > DateTimeOffset.UtcNow.AddMinutes(1))
                break;
            await Task.Delay(50);
        }

        await Task.Delay(250); // let any final increment land
        return counter.Runs;
    }

    [Fact]
    public async Task Skip_Runs_None_Of_The_Missed_Occurrences()
    {
        var counter = new Counter();
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.Skip);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBe(0); }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task FireNext_Behaves_Like_Skip()
    {
        var counter = new Counter();
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.FireNext);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBe(0); }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task FireOnce_Runs_Exactly_One_Catch_Up()
    {
        var counter = new Counter();
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.FireOnce);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBe(1); }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task FireAll_Runs_Every_Missed_Occurrence()
    {
        var counter = new Counter();
        // A 5-hour-stale hourly schedule has the stale occurrence plus ~5 hourly boundaries — FireAll runs the
        // whole backlog (clearly more than FireOnce's 1), then resumes at the next future occurrence.
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.FireAll);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBeGreaterThanOrEqualTo(5); }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task FireAll_Is_Bounded_By_MisfireCatchUpMax()
    {
        var counter = new Counter();
        // 5 missed, but the cap keeps only the most recent 2.
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.FireAll,
            o => o.MisfireCatchUpMax = 2);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBe(2); }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Default_Policy_Is_FireOnce()
    {
        var counter = new Counter();
        // The task inherits the default (no WithMisfirePolicy override kept: MisfirePolicy.Default) → FireOnce.
        using var host = Build(counter, await StaleStoreAsync(TimeSpan.FromHours(5)), MisfirePolicy.Default);
        await host.StartAsync();
        try { (await SettleAsync(host, counter)).ShouldBe(1); }
        finally { await host.StopAsync(); }
    }
}
