using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

/// <summary>
/// Retry policies: attempt counting, the backoff strategies and jitter, how a task's own policy layers over
/// the global default and the DefaultMaxRetries shorthand, and the retry context the job body and the hooks
/// see for each attempt.
/// </summary>
public class RetryPolicyTests
{
    // ---- The policy itself (no scheduler involved) ----------------------------------------------

    [Fact]
    public void MaxAttempts_Counts_The_First_Run()
    {
        var policy = new CronnerRetryPolicy(3, CronnerRetryStrategy.Immediate);
        policy.AllowsRetry(1).ShouldBeTrue("attempt 1 of 3 still has attempts left");
        policy.AllowsRetry(2).ShouldBeTrue();
        policy.AllowsRetry(3).ShouldBeFalse("the third attempt is the last one");

        CronnerRetryPolicy.None.MaxAttempts.ShouldBe(1);
        CronnerRetryPolicy.None.AllowsRetry(1).ShouldBeFalse("one attempt means no retry at all");
    }

    [Fact]
    public void An_Impossible_Policy_Is_Rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CronnerRetryPolicy(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new CronnerRetryPolicy(3, delay: TimeSpan.FromSeconds(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => new CronnerRetryPolicy(3, maxDelay: TimeSpan.Zero));
    }

    [Fact]
    public void Immediate_Never_Waits()
    {
        var policy = CronnerRetryPolicy.Immediate(4);
        policy.GetDelay(1).ShouldBe(TimeSpan.Zero);
        policy.GetDelay(3).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Fixed_Waits_The_Same_Delay_Every_Time()
    {
        var policy = CronnerRetryPolicy.FixedDelay(5, TimeSpan.FromSeconds(7));
        policy.GetDelay(1).ShouldBe(TimeSpan.FromSeconds(7));
        policy.GetDelay(4).ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void Fixed_Is_Used_As_Written_Even_Beyond_The_Default_Cap()
    {
        // MaxDelay exists to tame exponential growth; a delay typed out literally is honoured as typed.
        var policy = CronnerRetryPolicy.FixedDelay(3, TimeSpan.FromHours(4));
        policy.GetDelay(1).ShouldBe(TimeSpan.FromHours(4));
    }

    [Fact]
    public void Exponential_Doubles_The_Wait_After_Each_Failed_Attempt()
    {
        var policy = CronnerRetryPolicy.ExponentialBackoff(5, TimeSpan.FromSeconds(5));
        policy.GetDelay(1).ShouldBe(TimeSpan.FromSeconds(5));
        policy.GetDelay(2).ShouldBe(TimeSpan.FromSeconds(10));
        policy.GetDelay(3).ShouldBe(TimeSpan.FromSeconds(20));
        policy.GetDelay(4).ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void Exponential_Stops_Growing_At_MaxDelay()
    {
        var policy = CronnerRetryPolicy.ExponentialBackoff(50, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        policy.GetDelay(3).ShouldBe(TimeSpan.FromSeconds(20));
        policy.GetDelay(4).ShouldBe(TimeSpan.FromSeconds(30), "20s doubled is past the cap");
        policy.GetDelay(40).ShouldBe(TimeSpan.FromSeconds(30), "a long run of failures cannot overflow past the cap");
    }

    [Fact]
    public void Jitter_Spreads_The_Wait_Over_The_Upper_Half_Of_The_Delay()
    {
        var policy = CronnerRetryPolicy.FixedDelay(10, TimeSpan.FromSeconds(10), jitter: true);
        var draws = Enumerable.Range(0, 200).Select(_ => policy.GetDelay(1)).ToArray();

        draws.ShouldAllBe(d => d >= TimeSpan.FromSeconds(5) && d <= TimeSpan.FromSeconds(10));
        draws.Distinct().Count().ShouldBeGreaterThan(1, "jitter must actually vary the wait");
    }

    [Fact]
    public void Default_Strategy_Follows_Whether_A_Delay_Was_Configured()
    {
        new CronnerRetryPolicy(3).GetDelay(1).ShouldBe(TimeSpan.Zero, "no delay configured behaves as Immediate");
        new CronnerRetryPolicy(3, delay: TimeSpan.FromSeconds(3)).GetDelay(2)
            .ShouldBe(TimeSpan.FromSeconds(3), "a delay configured without a strategy behaves as Fixed");
    }

    // ---- Configuration surfaces ------------------------------------------------------------------

    [Fact]
    public void The_Fluent_Builder_Produces_The_Policy_It_Describes()
    {
        var registry = BuildRegistry(cronner => cronner.Sched<FailingJob>(
            x => x.Run(x.HasParam<ICronnerJobContext>()),
            o => o.WithId("job").WithRetryPolicy(p => p
                .MaxAttempts(5)
                .ExponentialBackoff(TimeSpan.FromSeconds(5))
                .WithJitter())));

        registry.TryGet("job", out var descriptor).ShouldBeTrue();
        var policy = descriptor!.RetryPolicy.ShouldNotBeNull();
        policy.MaxAttempts.ShouldBe(5);
        policy.Strategy.ShouldBe(CronnerRetryStrategy.Exponential);
        policy.Delay.ShouldBe(TimeSpan.FromSeconds(5));
        policy.Jitter.ShouldBeTrue();
        policy.MaxDelay.ShouldBe(CronnerRetryPolicy.DefaultMaxDelay);
    }

    [Fact]
    public void A_Policy_With_No_Attempt_Count_Defaults_To_Three_Attempts()
    {
        var registry = BuildRegistry(cronner => cronner.Sched<FailingJob>(
            x => x.Run(x.HasParam<ICronnerJobContext>()),
            o => o.WithId("job").WithRetryPolicy(p => p.FixedDelay(TimeSpan.FromSeconds(1)))));

        registry.TryGet("job", out var descriptor).ShouldBeTrue();
        descriptor!.RetryPolicy!.MaxAttempts.ShouldBe(3, "configuring a policy at all means retries are wanted");
    }

    [Fact]
    public void A_Task_Without_A_Policy_Inherits_Rather_Than_Carrying_One()
    {
        var registry = BuildRegistry(cronner => cronner.Sched<FailingJob>(
            x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        registry.TryGet("job", out var descriptor).ShouldBeTrue();
        descriptor!.RetryPolicy.ShouldBeNull();
    }

    [Fact]
    public void Attribute_Retry_Properties_Build_A_Policy()
    {
        var attribute = new CronnerTaskAttribute(cronstring: "0 * * * *")
        {
            MaxAttempts = 4,
            RetryStrategy = CronnerRetryStrategy.Exponential,
            RetryDelaySeconds = 2,
            RetryMaxDelaySeconds = 30,
            RetryJitter = true,
        };

        var policy = CronnerRetryPolicy.FromTaskAttribute("task", attribute).ShouldNotBeNull();
        policy.MaxAttempts.ShouldBe(4);
        policy.Strategy.ShouldBe(CronnerRetryStrategy.Exponential);
        policy.Delay.ShouldBe(TimeSpan.FromSeconds(2));
        policy.MaxDelay.ShouldBe(TimeSpan.FromSeconds(30));
        policy.Jitter.ShouldBeTrue();

        CronnerRetryPolicy.FromTaskAttribute("task", new CronnerTaskAttribute(cronstring: "0 * * * *"))
            .ShouldBeNull("a task that configures nothing inherits the default policy");
    }

    [Fact]
    public void Retry_Properties_Without_MaxAttempts_Fail_Fast()
    {
        // Silently ignoring them would leave a task that looks retried but never is.
        var attribute = new CronnerTaskAttribute(cronstring: "0 * * * *") { RetryDelaySeconds = 5 };
        var ex = Should.Throw<InvalidOperationException>(() => CronnerRetryPolicy.FromTaskAttribute("nightly", attribute));
        ex.Message.ShouldContain("nightly");
        ex.Message.ShouldContain("MaxAttempts");
    }

    [Fact]
    public async Task An_Attribute_Task_Carries_The_Policy_Its_Properties_Describe()
    {
        var host = new HostBuilder()
            .ConfigureServices(services => services.AddDotnetCronner(c => c
                .DisableAutoDiscovery()
                .AutoDiscoverFromAssembly(typeof(RetryPolicyTests))
                .AutoDiscoverFromType(typeof(IRetryProbeJob))))
            .Build();

        await host.StartAsync();
        try
        {
            var registry = host.Services.GetRequiredService<CronnerRegistry>();
            registry.TryGet("retry-probe", out var descriptor).ShouldBeTrue();
            var policy = descriptor!.RetryPolicy.ShouldNotBeNull();
            policy.MaxAttempts.ShouldBe(4);
            policy.Strategy.ShouldBe(CronnerRetryStrategy.Exponential);
            policy.Delay.ShouldBe(TimeSpan.FromSeconds(2));
            policy.Jitter.ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ---- The scheduler honouring the policy ------------------------------------------------------

    [Fact]
    public async Task A_Failing_Task_Runs_Exactly_MaxAttempts_Times()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(3).Immediate())));

        await RunToExhaustionAsync(host, seen);
        seen.Snapshot().Attempts.ShouldBe([1, 2, 3], "three total attempts — the first run plus two retries");
    }

    [Fact]
    public async Task A_Task_Policy_Overrides_The_Global_Default()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .WithRetryPolicy(p => p.MaxAttempts(5).Immediate())
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(2).Immediate())));

        await RunToExhaustionAsync(host, seen);
        seen.Snapshot().Attempts.ShouldBe([1, 2], "the task's own policy wins over the global default");
    }

    [Fact]
    public async Task The_Global_Default_Policy_Applies_To_A_Task_Without_One()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .WithRetryPolicy(p => p.MaxAttempts(3).Immediate())
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        await RunToExhaustionAsync(host, seen);
        seen.Snapshot().Attempts.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task The_DefaultMaxRetries_Shorthand_Still_Drives_Retries()
    {
        // Back-compat: an application that configured retries before policies existed keeps its behavior —
        // DefaultMaxRetries counts retries, so 2 of them is 3 attempts in total.
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")),
            o => { o.DefaultMaxRetries = 2; o.RetryDelay = TimeSpan.Zero; });

        await RunToExhaustionAsync(host, seen);
        seen.Snapshot().Attempts.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task The_Fail_Hook_Sees_The_Attempt_Count_And_The_Wait_Before_The_Next_One()
    {
        var seen = new Seen();
        var reported = new ConcurrentQueue<(int Attempt, int Max, bool WillRetry, TimeSpan? Delay)>();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx =>
            {
                reported.Enqueue((ctx.Attempt, ctx.MaxAttempts, ctx.WillRetry, ctx.RetryDelay));
                if (!ctx.WillRetry) seen.Done.TrySetResult(true);
                return Task.CompletedTask;
            })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(3).ExponentialBackoff(TimeSpan.FromMilliseconds(200)))));

        await RunToExhaustionAsync(host, seen);

        var rows = reported.OrderBy(r => r.Attempt).ToArray();
        rows.Length.ShouldBe(3);
        rows.ShouldAllBe(r => r.Max == 3);
        rows.Select(r => r.Attempt).ShouldBe([1, 2, 3]);
        rows.Select(r => r.WillRetry).ShouldBe([true, true, false]);
        rows[0].Delay.ShouldBe(TimeSpan.FromMilliseconds(200));
        rows[1].Delay.ShouldBe(TimeSpan.FromMilliseconds(400), "the wait doubles after each failed attempt");
        rows[2].Delay.ShouldBeNull("the last attempt is not followed by a wait");
    }

    [Fact]
    public async Task A_Retry_Sees_Why_The_Previous_Attempt_Failed()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(3).Immediate())));

        await RunToExhaustionAsync(host, seen);

        var errors = seen.Snapshot().PreviousErrors;
        errors.Count.ShouldBe(3);
        errors[0].ShouldBeNull("the first attempt has no previous failure");
        errors[1].ShouldBe("boom");
        errors[2].ShouldBe("boom");
    }

    [Fact]
    public async Task The_Retry_Delay_Is_Actually_Waited_Out()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(2).FixedDelay(TimeSpan.FromSeconds(1)))));

        var started = DateTimeOffset.UtcNow;
        await RunToExhaustionAsync(host, seen);

        seen.Snapshot().Attempts.ShouldBe([1, 2]);
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(900), "the retry waits out its delay");
    }

    [Fact]
    public async Task Each_Attempt_Is_Its_Own_Execution_In_The_History()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .WithExecutionHistory(10)
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(3).Immediate())));

        await RunToExhaustionAsync(host, seen, keepHostAlive: true);

        var history = (await host.Services.GetRequiredService<ICronnerClient>().GetExecutionsAsync("job", 10))
            .OrderBy(e => e.Attempt).ToArray();
        await host.StopAsync();

        history.Select(e => e.Attempt).ShouldBe([1, 2, 3]);
        history.Select(e => e.Id).Distinct().Count().ShouldBe(3, "a retry stays distinguishable from a fresh occurrence");
        history.ShouldAllBe(e => e.Status == JobExecutionStatus.Failed);
    }

    [Fact]
    public async Task A_Retry_That_Succeeds_Ends_The_Attempts()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FlakyJob>(x => x.Run(x.HasParam<ICronnerJobContext>()),
                o => o.WithId("job").WithRetryPolicy(p => p.MaxAttempts(5).Immediate())));

        await RunToExhaustionAsync(host, seen);
        seen.Snapshot().Attempts.ShouldBe([1, 2], "the second attempt succeeds, so the remaining three are not used");
    }

    [Fact]
    public async Task A_Concurrent_Task_Reports_That_No_Retry_Is_Coming()
    {
        // A Concurrent run only records its outcome — the schedule already advanced — so no further attempt
        // is ever dispatched. WillRetry must say so rather than promising a retry that never arrives.
        var seen = new Seen();
        var reported = new ConcurrentQueue<(int Attempt, int Max, bool WillRetry)>();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx =>
            {
                reported.Enqueue((ctx.Attempt, ctx.MaxAttempts, ctx.WillRetry));
                seen.Done.TrySetResult(true);
                return Task.CompletedTask;
            })
            .Sched<FailingJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o
                .WithId("job")
                .WithConcurrency(CronnerConcurrencyMode.Concurrent)
                .WithRetryPolicy(p => p.MaxAttempts(3).Immediate())));

        await RunToExhaustionAsync(host, seen);

        var rows = reported.ToArray();
        rows.Length.ShouldBe(1, "the run is never retried");
        rows[0].WillRetry.ShouldBeFalse();
        rows[0].Max.ShouldBe(1);
    }

    // ---- Harness ---------------------------------------------------------------------------------

    public sealed class Seen
    {
        private readonly List<int> _attempts = [];
        private readonly List<string?> _previousErrors = [];

        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(ICronnerJobContext ctx)
        {
            lock (_attempts)
            {
                _attempts.Add(ctx.Attempt);
                _previousErrors.Add(ctx.PreviousError);
            }
        }

        public (IReadOnlyList<int> Attempts, IReadOnlyList<string?> PreviousErrors) Snapshot()
        {
            lock (_attempts)
                return (_attempts.ToArray(), _previousErrors.ToArray());
        }
    }

    // Discovered only via the marker interface, so it never joins the other assembly-wide discovery tests.
    public interface IRetryProbeJob;

    public sealed class AttributeRetryJob : IRetryProbeJob
    {
        [CronnerTask(id: "retry-probe", cronstring: "0 0 * * *",
            MaxAttempts = 4, RetryStrategy = CronnerRetryStrategy.Exponential, RetryDelaySeconds = 2, RetryJitter = true)]
        public void Run(CancellationToken ct) { }
    }

    public sealed class FailingJob(Seen seen)
    {
        public void Run(ICronnerJobContext ctx)
        {
            seen.Record(ctx);
            throw new InvalidOperationException("boom");
        }
    }

    public sealed class FlakyJob(Seen seen)
    {
        public void Run(ICronnerJobContext ctx)
        {
            seen.Record(ctx);
            if (ctx.Attempt < 2)
                throw new InvalidOperationException("boom");
        }
    }

    private static IHost BuildHost(Seen seen, Action<ICronnerBuilder> configure, Action<CronnerOptions>? options = null) =>
        new HostBuilder()
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

    private static CronnerRegistry BuildRegistry(Action<ICronnerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDotnetCronner(cronner =>
        {
            cronner.Configure(o => o.ScanEntryAssembly = false);
            configure(cronner);
        });
        return services.BuildServiceProvider().GetRequiredService<CronnerRegistry>();
    }

    private static async Task RunToExhaustionAsync(IHost host, Seen seen, bool keepHostAlive = false)
    {
        await host.StartAsync();
        var stop = true;
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            (await Task.WhenAny(seen.Done.Task, Task.Delay(TimeSpan.FromSeconds(20))))
                .ShouldBeSameAs(seen.Done.Task, "timed out waiting for the attempts to finish");
            await Task.Delay(300);   // let the final history write and reschedule land
            stop = !keepHostAlive;
        }
        finally
        {
            if (stop)
                await host.StopAsync();
        }
    }
}
