using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

// The execution-history test below still exercises the deprecated Data / SetExecutionData slot to prove it
// keeps working during deprecation. Suppress the obsolete-usage error here only.
#pragma warning disable CS0618

namespace DotnetCronner.Tests;

public class Cronner005Tests
{
    private static IHost BuildHost(Action<IServiceCollection> services, Action<ICronnerBuilder> cronner) =>
        new HostBuilder()
            .ConfigureServices(s =>
            {
                services(s);
                s.AddDotnetCronner(c =>
                {
                    c.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner(c);
                });
            })
            .Build();

    // ---- #2 run-state bag ----------------------------------------------------------------------

    public sealed record Marker(string Value);

    public sealed class BagJob
    {
        public void Run(ICronnerJobContext ctx)
        {
            ctx.Set(new Marker("from-job"));
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public async Task RunState_Set_By_Job_Is_Readable_In_OnFail()
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(
            s => s.AddSingleton<BagJob>(),
            c => c
                // The failure path is where it matters — the hook reads what the job stashed, even though the
                // job threw and its scope is unwinding.
                .OnFail(ctx => { seen.TrySetResult(ctx.Get<Marker>()?.Value); return Task.CompletedTask; })
                .Sched<BagJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("bag")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("bag");
            (await Task.WhenAny(seen.Task, Task.Delay(5000))).ShouldBeSameAs(seen.Task);
            (await seen.Task).ShouldBe("from-job");
        }
        finally { await host.StopAsync(); }
    }

    // ---- #1 hook scope -------------------------------------------------------------------------

    public sealed class ScopedBag { public string? Value { get; set; } }

    public sealed class ScopeWriterJob
    {
        public void Run(ScopedBag bag) => bag.Value = "job";
    }

    [Fact]
    public async Task Isolated_HookScope_Gives_The_Hook_Its_Own_Scope()
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(
            s => s.AddScoped<ScopedBag>(),
            c => c
                .Configure(o => o.HookScope = CronnerHookScope.Isolated)
                // Isolated → the hook resolves a FRESH ScopedBag, not the one the job wrote → Value is null.
                .OnSuccess(ctx => { seen.TrySetResult(ctx.HasParam<ScopedBag>().Value); return Task.CompletedTask; })
                .Sched<ScopeWriterJob>(x => x.Run(x.HasParam<ScopedBag>()), o => o.WithId("iso")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("iso");
            (await Task.WhenAny(seen.Task, Task.Delay(5000))).ShouldBeSameAs(seen.Task);
            (await seen.Task).ShouldBeNull(); // a fresh scope, so the job's write is not visible
        }
        finally { await host.StopAsync(); }
    }

    // ---- #1 per-hook scope ---------------------------------------------------------------------

    public sealed class ScopedProbe { public string? Value { get; set; } }

    public sealed class ProbeJob
    {
        public void Run(ScopedProbe probe) => probe.Value = "job";
    }

    public sealed class ProbeHook(ScopedProbe probe, TaskCompletionSource<string?> sink) : ICronnerTaskHook
    {
        public Task OnSuccessAsync(CronnerTaskContext context)
        {
            sink.TrySetResult(probe.Value); // "job" if it shares the job's scope; null if it got a fresh one
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Per_Hook_Isolated_Overrides_The_Shared_Default()
    {
        var sink = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(
            s => { s.AddScoped<ScopedProbe>(); s.AddSingleton(sink); },
            // Default stays Shared, but THIS hook is registered Isolated → it resolves a fresh ScopedProbe.
            c => c
                .AddHook<ProbeHook>(CronnerHookScope.Isolated)
                .Sched<ProbeJob>(x => x.Run(x.HasParam<ScopedProbe>()), o => o.WithId("probe")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("probe");
            (await Task.WhenAny(sink.Task, Task.Delay(5000))).ShouldBeSameAs(sink.Task);
            (await sink.Task).ShouldBeNull(); // its own scope — the job's write is invisible
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Hook_Without_A_Scope_Uses_The_Shared_Default()
    {
        var sink = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(
            s => { s.AddScoped<ScopedProbe>(); s.AddSingleton(sink); },
            // No per-hook scope → inherits the default (Shared) → shares the job's scope.
            c => c
                .AddHook<ProbeHook>()
                .Sched<ProbeJob>(x => x.Run(x.HasParam<ScopedProbe>()), o => o.WithId("probe")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("probe");
            (await Task.WhenAny(sink.Task, Task.Delay(5000))).ShouldBeSameAs(sink.Task);
            (await sink.Task).ShouldBe("job"); // same scope as the job
        }
        finally { await host.StopAsync(); }
    }

    // ---- #5 WillRetry on the fail context ------------------------------------------------------

    public sealed class AlwaysFailJob
    {
        public void Run(CancellationToken ct) => throw new InvalidOperationException("nope");
    }

    [Fact]
    public async Task OnFail_WillRetry_Is_True_Until_The_Final_Attempt()
    {
        var flags = new List<bool>();
        using var host = BuildHost(
            s => s.AddSingleton<AlwaysFailJob>(),
            c => c
                .Configure(o => { o.DefaultMaxRetries = 1; o.RetryDelay = TimeSpan.Zero; })
                .OnFail(ctx => { lock (flags) flags.Add(ctx.WillRetry); return Task.CompletedTask; })
                .Sched<AlwaysFailJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("retry")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("retry");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (flags)
                    if (flags.Count >= 2)
                        break;
                await Task.Delay(50);
            }

            lock (flags)
            {
                flags.Count.ShouldBe(2);       // one attempt + one retry
                flags[0].ShouldBeTrue();        // a retry is still coming
                flags[1].ShouldBeFalse();       // the final attempt — this is where you'd alert
            }
        }
        finally { await host.StopAsync(); }
    }

    // ---- #4 fail-fast on a never-firing cron ---------------------------------------------------

    public sealed class NoopJob
    {
        public void Run(CancellationToken ct) { }
    }

    [Fact]
    public async Task OnInvalidSchedule_Throw_Refuses_To_Start()
    {
        using var host = BuildHost(
            _ => { },
            c => c
                .Configure(o => o.OnInvalidSchedule = CronnerInvalidScheduleBehavior.Throw)
                // 31 February — parses, never occurs.
                .Sched<NoopJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithCron("0 0 31 2 *").WithId("never")));

        await Should.ThrowAsync<InvalidOperationException>(async () => await host.StartAsync());
    }

    [Fact]
    public async Task OnInvalidSchedule_MarkFailed_Is_The_Default_And_Still_Boots()
    {
        using var host = BuildHost(
            _ => { },
            c => c.Sched<NoopJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithCron("0 0 31 2 *").WithId("never")));

        await host.StartAsync(); // does not throw
        try
        {
            var job = await host.Services.GetRequiredService<ICronnerClient>().GetTaskByIdAsync("never");
            job!.State.ShouldBe(CronnerTaskState.Failed);
        }
        finally { await host.StopAsync(); }
    }

    // ---- progress custom payload (total + scope) -----------------------------------------------

    public sealed class ProgressPayloadCollector
    {
        private readonly object _gate = new();
        private readonly List<object?> _total = [];
        private readonly List<object?> _scope = [];

        public void AddTotal(object? payload) { lock (_gate) _total.Add(payload); }
        public void AddScope(object? payload) { lock (_gate) _scope.Add(payload); }
        public bool Has(string total, string scope) { lock (_gate) return _total.Contains(total) && _scope.Contains(scope); }
    }

    public sealed class ProgressCaptureHook(ProgressPayloadCollector collector) : ICronnerTaskHook
    {
        public Task OnTotalProgressChangeAsync(CronnerTaskContext context) { collector.AddTotal(context.ProgressPayload); return Task.CompletedTask; }
        public Task OnScopeProgressAsync(CronnerTaskContext context) { collector.AddScope(context.ProgressPayload); return Task.CompletedTask; }
    }

    public sealed class ProgressPayloadJob
    {
        public void Run(ICronnerJobContext ctx, CancellationToken ct)
        {
            ctx.Progress(0.5m, "halfway");                          // total progress + payload
            using var scope = ctx.OpenProgressScope("phase");
            scope.Progress(1m, "phase-done");                      // scope progress + payload
        }
    }

    [Fact]
    public async Task Progress_Reports_Deliver_A_Custom_Payload_To_Hooks()
    {
        var collector = new ProgressPayloadCollector();
        using var host = BuildHost(
            s => { s.AddSingleton(collector); s.AddSingleton<ProgressPayloadJob>(); },
            c => c
                .AddHook<ProgressCaptureHook>()
                .Sched<ProgressPayloadJob>(
                    x => x.Run(x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
                    o => o.WithId("prog")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("prog");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !collector.Has("halfway", "phase-done"))
                await Task.Delay(50);

            collector.Has("halfway", "phase-done").ShouldBeTrue(); // both total and scope payloads arrived
        }
        finally { await host.StopAsync(); }
    }

    // ---- #3 execution-history owner + consumer data --------------------------------------------

    public sealed record RunSummary(int Processed, string Note);

    public sealed class SummaryJob
    {
        public void Run(ICronnerJobContext ctx) => ctx.SetExecutionData(new RunSummary(7, "ok"));
    }

    [Fact]
    public async Task Execution_Records_Owner_And_Consumer_Data()
    {
        using var host = BuildHost(
            s => s.AddSingleton<SummaryJob>(),
            c => c
                .WithExecutionHistory(10)
                .Sched<SummaryJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("sum")));

        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            await client.TriggerNowAsync("sum");

            CronnerJobExecution? run = null;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                run = (await client.GetExecutionsAsync("sum")).FirstOrDefault(r => r.Status != JobExecutionStatus.Running);
                if (run is not null)
                    break;
                await Task.Delay(50);
            }

            run.ShouldNotBeNull();
            run!.Status.ShouldBe(JobExecutionStatus.Succeeded);
            run.Owner.ShouldNotBeNullOrEmpty();          // which node ran it
            run.Data.ShouldNotBeNull();
            run.Data!.ShouldContain("ok");                // the consumer summary was persisted
            run.Data.ShouldContain("7");
        }
        finally { await host.StopAsync(); }
    }
}
