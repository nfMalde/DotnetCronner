using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class HookTests
{
    public sealed class OkJob
    {
        public void Run(CancellationToken ct) { }
    }

    public sealed class FailingJob
    {
        public void Run(CancellationToken ct) => throw new InvalidOperationException("boom");
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
    public async Task OnSuccess_Fires_AfterSuccessfulRun()
    {
        var succeeded = new TaskCompletionSource<CronnerTaskContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .OnSuccess(ctx => { succeeded.TrySetResult(ctx); return Task.CompletedTask; })
            .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("ok")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("ok");

            var completed = await Task.WhenAny(succeeded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(succeeded.Task);
            var ctx = await succeeded.Task;
            ctx.Job.Id.ShouldBe("ok");
            ctx.Exception.ShouldBeNull();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OnFail_Fires_WithException()
    {
        var failed = new TaskCompletionSource<CronnerTaskContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .OnFail(ctx => { failed.TrySetResult(ctx); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("fail")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("fail");

            var completed = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(failed.Task);
            var ctx = await failed.Task;
            ctx.Exception.ShouldBeOfType<InvalidOperationException>();
            ctx.Exception!.Message.ShouldBe("boom");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Hook_Instance_Receives_Starting_And_Succeeded()
    {
        var hook = new RecordingHook();
        using var host = BuildHost(cronner => cronner
            .AddHook(hook)
            .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("rec")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("rec");

            var completed = await Task.WhenAny(hook.Done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBeSameAs(hook.Done.Task);
            hook.Events.ShouldBe(new[] { "starting", "succeeded" });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class RecordingHook : ICronnerTaskHook
    {
        public List<string> Events { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnStartAsync(CronnerTaskContext context)
        {
            Events.Add("starting");
            return Task.CompletedTask;
        }

        public Task OnSuccessAsync(CronnerTaskContext context)
        {
            Events.Add("succeeded");
            Done.TrySetResult();
            return Task.CompletedTask;
        }
    }

    public sealed class SlowJob
    {
        public Task Run(CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(1300), ct);
    }

    [Fact]
    public async Task LockLifecycle_Hooks_Fire_InOrder_AroundRun()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .OnLockAcquire(_ => { events.Enqueue("acquire"); return Task.CompletedTask; })
            .OnStart(_ => { events.Enqueue("start"); return Task.CompletedTask; })
            .OnSuccess(_ => { events.Enqueue("success"); return Task.CompletedTask; })
            .OnLockRelease(_ => { events.Enqueue("release"); released.TrySetResult(); return Task.CompletedTask; })
            .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("lock")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("lock");

            (await Task.WhenAny(released.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(released.Task);
            events.ShouldBe(new[] { "acquire", "start", "success", "release" });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PerSchedule_Hook_Fires_ForThatTask()
    {
        var done = new TaskCompletionSource<CronnerTaskContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o
                .WithId("perjob")
                .OnSuccess(ctx => { done.TrySetResult(ctx); return Task.CompletedTask; })));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("perjob");

            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(done.Task);
            (await done.Task).Job.Id.ShouldBe("perjob");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public sealed class Recorder
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Hits { get; } = new();
    }

    public sealed class ExprHook
    {
        public void Handle(Recorder recorder) => recorder.Hits.Enqueue("expr-start");
    }

    [Fact]
    public async Task Expression_Hook_Invokes_Method_Resolving_HasParam()
    {
        var recorder = new Recorder();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(recorder);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner
                        .OnSuccess(_ => { done.TrySetResult(); return Task.CompletedTask; })
                        .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o
                            .WithId("expr")
                            .OnStart<ExprHook>(h => h.Handle(h.HasParam<Recorder>())));
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("expr");

            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(done.Task);
            recorder.Hits.ShouldContain("expr-start");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Context_HasParam_Resolves_From_Hook_Scope()
    {
        var resolved = new TaskCompletionSource<Recorder>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new Recorder();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(recorder);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner
                        .OnSuccess(ctx => { resolved.TrySetResult(ctx.HasParam<Recorder>()); return Task.CompletedTask; })
                        .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("ctxparam"));
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("ctxparam");

            (await Task.WhenAny(resolved.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(resolved.Task);
            (await resolved.Task).ShouldBeSameAs(recorder);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task KeepAlive_Hook_Fires_ForLongRunningTask()
    {
        var keepAlives = 0;
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .Configure(o => o.LockTtl = TimeSpan.FromSeconds(1))   // heartbeat interval = max(1000ms, 500ms)
            .OnKeepAlive(_ => { Interlocked.Increment(ref keepAlives); return Task.CompletedTask; })
            .OnSuccess(_ => { success.TrySetResult(); return Task.CompletedTask; })
            .Sched<SlowJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("slow")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("slow");

            (await Task.WhenAny(success.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(success.Task);
            keepAlives.ShouldBeGreaterThan(0);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // Wraps the in-memory store but fails every lock renewal AFTER the run has started (the engine confirms the
    // claim with one renewal right before it starts a run; that one succeeds), forcing the lost-lock path mid-run.
    private sealed class LockLosingStore : ICronnerStore
    {
        private readonly InMemoryCronnerStore _inner = new();
        private int _renewals;

        public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default) => _inner.GetAsync(state, offset, limit, ct);
        public Task UpsertAsync(CronnerJob job, CancellationToken ct = default) => _inner.UpsertAsync(job, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => _inner.RemoveAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default) => _inner.AcquireDueAsync(now, owner, lockTtl, max, ct);
        public Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default) =>
            Interlocked.Increment(ref _renewals) == 1 ? _inner.RenewLockAsync(id, owner, lockedUntil, ct) : Task.FromResult(false);
        public Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken ct = default) => _inner.ReleaseLockAsync(id, owner, ct);
    }

    [Fact]
    public async Task OnLockLost_Fires_WhenRenewalFails()
    {
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(cronner => cronner
            .UseStore<LockLosingStore>()
            .Configure(o => o.LockTtl = TimeSpan.FromSeconds(1))   // first heartbeat at ~1s fails renewal
            .OnLockLost(_ => { lost.TrySetResult(); return Task.CompletedTask; })
            .Sched<SlowJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("losing")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("losing");

            (await Task.WhenAny(lost.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(lost.Task);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public sealed class LongJob
    {
        public Task Run(CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    [Fact]
    public async Task Slow_KeepAlive_Hook_Does_Not_Perturb_Renewal_Or_Lose_The_Lock()
    {
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockLost = false;

        using var host = BuildHost(cronner => cronner
            // interval = 1s. If the keepalive hook were in the renewal path, 1s + 1.2s > 2s TTL would lose the lock.
            .Configure(o => o.LockTtl = TimeSpan.FromSeconds(2))
            .OnKeepAlive(_ => Task.Delay(TimeSpan.FromMilliseconds(1200)))   // deliberately slow, ignores cancellation
            .OnLockLost(_ => { lockLost = true; return Task.CompletedTask; })
            .OnSuccess(_ => { success.TrySetResult(); return Task.CompletedTask; })
            .Sched<LongJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("long")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("long");

            (await Task.WhenAny(success.Task, Task.Delay(TimeSpan.FromSeconds(15)))).ShouldBeSameAs(success.Task);
            lockLost.ShouldBeFalse();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public sealed class ProgressJob
    {
        public async Task Run(ICronnerJobContext ctx, CancellationToken token)
        {
            await ctx.ProgressAsync(0.25m);
            await using (var scope = ctx.OpenProgressScope("files"))
                await scope.ProgressAsync(0.5m);
            await ctx.ProgressAsync(1.0m);
        }
    }

    [Fact]
    public async Task Progress_Reporting_Fires_ProgressHooks()
    {
        var totals = new System.Collections.Concurrent.ConcurrentQueue<decimal>();
        var opened = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var scopeProgress = new System.Collections.Concurrent.ConcurrentQueue<(string? Category, decimal Value)>();
        var closed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(cronner => cronner
            .OnTotalProgressChange(ctx => { totals.Enqueue(ctx.TotalProgress); return Task.CompletedTask; })
            .OnProgressScopeOpened(ctx => { opened.Enqueue(ctx.ProgressScope!.Category); return Task.CompletedTask; })
            .OnScopeProgress(ctx => { scopeProgress.Enqueue((ctx.ProgressScope!.Category, ctx.ProgressScope!.Value)); return Task.CompletedTask; })
            .OnProgressScopeClosed(ctx => { closed.Enqueue(ctx.ProgressScope!.Category); return Task.CompletedTask; })
            .OnSuccess(_ => { done.TrySetResult(); return Task.CompletedTask; })
            .Sched<ProgressJob>(x => x.Run(x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()), o => o.WithId("prog")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("prog");

            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(done.Task);
            totals.ShouldBe(new[] { 0.25m, 1.0m });
            opened.ShouldBe(new string?[] { "files" });
            scopeProgress.ShouldBe(new (string?, decimal)[] { ("files", 0.5m) });
            closed.ShouldBe(new string?[] { "files" });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    public sealed class SyncProgressJob
    {
        public void Run(ICronnerJobContext ctx)
        {
            ctx.Progress(0.3m);
            using var scope = ctx.OpenProgressScope("phase");
            scope.Progress(0.7m);
        }
    }

    [Fact]
    public async Task Sync_Progress_Hooks_Are_Drained_Before_Terminal()
    {
        var totals = 0;
        var scopeEvents = 0;
        var seenAtSuccess = new TaskCompletionSource<(int Totals, int Scope)>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(cronner => cronner
            .OnTotalProgressChange(_ => { Interlocked.Increment(ref totals); return Task.CompletedTask; })
            .OnProgressScopeOpened(_ => { Interlocked.Increment(ref scopeEvents); return Task.CompletedTask; })
            .OnScopeProgress(_ => { Interlocked.Increment(ref scopeEvents); return Task.CompletedTask; })
            .OnProgressScopeClosed(_ => { Interlocked.Increment(ref scopeEvents); return Task.CompletedTask; })
            // By the time OnSuccess runs, fire-and-forget progress hooks must already be drained.
            .OnSuccess(_ => { seenAtSuccess.TrySetResult((Volatile.Read(ref totals), Volatile.Read(ref scopeEvents))); return Task.CompletedTask; })
            .Sched<SyncProgressJob>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("sync")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("sync");

            (await Task.WhenAny(seenAtSuccess.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(seenAtSuccess.Task);
            var (totalsAtSuccess, scopeAtSuccess) = await seenAtSuccess.Task;
            totalsAtSuccess.ShouldBe(1);          // ctx.Progress(0.3m)
            scopeAtSuccess.ShouldBe(3);           // opened + progress + closed
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
