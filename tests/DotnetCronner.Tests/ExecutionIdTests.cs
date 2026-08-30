using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

// This file still validates the deprecated execution-data slot (SetExecutionData / TryGetExecutionData / Data)
// so we know it keeps working during deprecation. Suppress the obsolete-usage error here only.
#pragma warning disable CS0618

namespace DotnetCronner.Tests;

/// <summary>
/// Wishlist 0.0.7 §1/§2/§3b: the execution id is visible to the job body and to every hook of the run and matches
/// the history row; a retry is a new execution; execution data can be read back; the hook context carries the
/// effective lock TTL and keepalive interval.
/// </summary>
public class ExecutionIdTests
{
    public sealed class Seen
    {
        public readonly ConcurrentDictionary<string, string> Ids = new();   // where → execution id
        public readonly ConcurrentBag<string> JobIds = [];
        public readonly TaskCompletionSource<CronnerTaskContext> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<CronnerTaskContext> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Summary
    {
        public int Processed { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Job(Seen seen)
    {
        public async Task Run(int delayMs, ICronnerJobContext ctx, CancellationToken ct)
        {
            seen.JobIds.Add(ctx.ExecutionId);
            seen.Ids["job"] = ctx.ExecutionId;
            ctx.SetExecutionData(new Summary { Processed = 3 });
            await Task.Delay(delayMs, ct);
        }
    }

    public sealed class FailingJob(Seen seen)
    {
        public void Run(ICronnerJobContext ctx)
        {
            seen.JobIds.Add(ctx.ExecutionId);
            throw new InvalidOperationException("boom");
        }
    }

    private static IHost BuildHost(Seen seen, Action<ICronnerBuilder> configure, Action<CronnerOptions>? options = null)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(seen);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                        options?.Invoke(o);
                    });
                    configure(cronner);
                });
            })
            .Build();
    }

    private static async Task<T> Within<T>(Task<T> task, int seconds)
    {
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)))).ShouldBeSameAs(task, "timed out");
        return await task;
    }

    [Fact]
    public async Task ExecutionId_Is_The_Same_In_The_Job_Every_Hook_And_The_History_Row()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .WithExecutionHistory(10)
            .OnLockAcquire(ctx => { seen.Ids["lock-acquire"] = ctx.ExecutionId; return Task.CompletedTask; })
            .OnStart(ctx => { seen.Ids["start"] = ctx.ExecutionId; return Task.CompletedTask; })
            .OnKeepAlive(ctx => { seen.Ids["keepalive"] = ctx.ExecutionId; return Task.CompletedTask; })
            .OnSuccess(ctx => { seen.Ids["success"] = ctx.ExecutionId; seen.Done.TrySetResult(ctx); return Task.CompletedTask; })
            .OnLockRelease(ctx => { seen.Ids["lock-release"] = ctx.ExecutionId; seen.Released.TrySetResult(ctx); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(1500, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()), o => o.WithId("job")),
            o => o.LockTtl = TimeSpan.FromSeconds(2)); // keepalive at ~1s, inside the 1.5s run

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Done.Task, 10);
            await Within(seen.Released.Task, 5);

            var id = seen.Ids["job"];
            id.ShouldNotBeNullOrEmpty();
            foreach (var where in new[] { "lock-acquire", "start", "keepalive", "success", "lock-release" })
                seen.Ids.ShouldContainKeyAndValue(where, id, $"{where} saw a different execution id");

            var history = await host.Services.GetRequiredService<ICronnerClient>().GetExecutionsAsync("job", 10);
            history.ShouldHaveSingleItem().Id.ShouldBe(id);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ExecutionId_Exists_Even_When_History_Is_Off()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(ctx => { seen.Ids["success"] = ctx.ExecutionId; seen.Done.TrySetResult(ctx); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(10, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()), o => o.WithId("job")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Done.Task, 10);
            seen.Ids["job"].ShouldNotBeNullOrEmpty();
            seen.Ids["success"].ShouldBe(seen.Ids["job"]);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Retry_Is_A_New_Execution_With_A_New_Id_And_The_Next_Attempt_Number()
    {
        var seen = new Seen();
        var fails = new List<(string Id, bool WillRetry)>();
        using var host = BuildHost(seen, cronner => cronner
            .WithExecutionHistory(10)
            .OnFail(ctx =>
            {
                lock (fails) fails.Add((ctx.ExecutionId, ctx.WillRetry));
                if (!ctx.WillRetry) seen.Done.TrySetResult(ctx);
                return Task.CompletedTask;
            })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")),
            o => { o.DefaultMaxRetries = 1; o.RetryDelay = TimeSpan.Zero; });

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Done.Task, 10);
            await Task.Delay(300);

            seen.JobIds.Count.ShouldBe(2);
            seen.JobIds.Distinct().Count().ShouldBe(2, "each attempt is its own execution");
            fails.Select(f => f.Id).ShouldBe(seen.JobIds.Reverse().ToArray(), ignoreOrder: true);
            fails.Select(f => f.WillRetry).ShouldBe([true, false]);

            var history = (await host.Services.GetRequiredService<ICronnerClient>().GetExecutionsAsync("job", 10)).OrderBy(e => e.Attempt).ToArray();
            history.Length.ShouldBe(2);
            history[0].Attempt.ShouldBe(1);
            history[1].Attempt.ShouldBe(2);
            history.Select(e => e.Id).ShouldBe(fails.Select(f => f.Id).ToArray(), ignoreOrder: true);
            history.ShouldAllBe(e => e.Status == JobExecutionStatus.Failed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TryGetExecutionData_Reads_Back_What_The_Job_Set_So_A_Hook_Can_Augment_It()
    {
        var seen = new Seen();
        var readBack = false;
        using var host = BuildHost(seen, cronner => cronner
            .WithExecutionHistory(10)
            .OnSuccess(ctx =>
            {
                readBack = ctx.TryGetExecutionData<Summary>(out var summary);
                if (readBack)
                {
                    summary.Note = "augmented by OnSuccess";
                    ctx.SetExecutionData(summary);
                }

                seen.Done.TrySetResult(ctx);
                return Task.CompletedTask;
            })
            .Sched<Job>(x => x.Run(10, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()), o => o.WithId("job")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            var ctx = await Within(seen.Done.Task, 10);
            readBack.ShouldBeTrue();
            ctx.TryGetExecutionData<string>(out _).ShouldBeFalse("the slot holds a Summary, not a string");
            await Task.Delay(300);

            var row = (await host.Services.GetRequiredService<ICronnerClient>().GetExecutionsAsync("job", 10)).ShouldHaveSingleItem();
            row.Data.ShouldNotBeNull();
            var persisted = JsonSerializer.Deserialize<Summary>(row.Data!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            persisted.Processed.ShouldBe(3);
            persisted.Note.ShouldBe("augmented by OnSuccess");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(null, 1000)]     // default: LockTtl / 2
    [InlineData(700, 700)]       // pinned
    public async Task Hook_Context_Carries_The_Effective_Lock_Ttl_And_KeepAlive_Interval(int? keepAliveMs, int expectedKeepAliveMs)
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner =>
            {
                cronner
                    .OnSuccess(ctx => { seen.Done.TrySetResult(ctx); return Task.CompletedTask; })
                    .Sched<Job>(x => x.Run(10, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()), o => o.WithId("job"));
                if (keepAliveMs is { } ms)
                    cronner.WithKeepAliveInterval(TimeSpan.FromMilliseconds(ms));
            },
            o => o.LockTtl = TimeSpan.FromSeconds(2));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            var ctx = await Within(seen.Done.Task, 10);
            ctx.LockTtl.ShouldBe(TimeSpan.FromSeconds(2));
            ctx.KeepAliveInterval.ShouldBe(TimeSpan.FromMilliseconds(expectedKeepAliveMs));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
