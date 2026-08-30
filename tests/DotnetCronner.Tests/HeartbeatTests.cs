using DotnetCronner.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

/// <summary>
/// The lease rule of the keepalive loop: a renewal the store cannot answer (throws / hangs) is not a lost lock
/// while the last confirmed expiry is ahead, but the run is abandoned BEFORE that expiry can lapse; a renewal
/// answered <c>false</c> is an immediate loss; a claim the store no longer confirms at run start is not run at all.
/// </summary>
public class HeartbeatTests
{
    /// <summary>A store whose <see cref="RenewLockAsync"/> follows a script keyed by call number (1 = the confirmation right before the run).</summary>
    private sealed class ScriptedRenewStore(Func<int, ICronnerStore, string, string, DateTimeOffset, CancellationToken, Task<bool>> script)
        : ForwardingStore(new InMemoryCronnerStore())
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>The expiry the engine last got a <c>true</c> for — the lease it may rely on.</summary>
        public DateTimeOffset? ConfirmedUntil { get; private set; }

        public override async Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default)
        {
            var renewed = await script(Interlocked.Increment(ref _calls), Inner, id, owner, lockedUntil, ct);
            if (renewed)
                ConfirmedUntil = lockedUntil;
            return renewed;
        }
    }

    public sealed class Probe
    {
        public readonly TaskCompletionSource<DateTimeOffset> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<DateTimeOffset> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<DateTimeOffset> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<CronnerTaskContext> LockLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int KeepAlives;
        public int Successes;
        public int Cancels;
    }

    public sealed class LongJob(Probe probe)
    {
        public async Task Run(int seconds, CancellationToken ct)
        {
            probe.Started.TrySetResult(DateTimeOffset.UtcNow);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                probe.Completed.TrySetResult(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                probe.Cancelled.TrySetResult(DateTimeOffset.UtcNow);
                throw;
            }
        }
    }

    private static IHost BuildHost(ScriptedRenewStore store, Probe probe, int jobSeconds, Action<CronnerOptions>? configure = null)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<ForwardingStore>(store);
                services.AddDotnetCronner(cronner => cronner
                    .UseStore<ForwardingStore>()
                    .Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                        o.LockTtl = TimeSpan.FromSeconds(8);   // keepalive every 4s, retry every 1s after a failure — margins wide enough for a noisy CI runner
                        configure?.Invoke(o);
                    })
                    .WithExecutionHistory(10)
                    .OnKeepAlive(_ => { Interlocked.Increment(ref probe.KeepAlives); return Task.CompletedTask; })
                    .OnSuccess(_ => { Interlocked.Increment(ref probe.Successes); return Task.CompletedTask; })
                    .OnCancel(_ => { Interlocked.Increment(ref probe.Cancels); return Task.CompletedTask; })
                    .OnLockLost(ctx => { probe.LockLost.TrySetResult(ctx); return Task.CompletedTask; })
                    .Sched<LongJob>(x => x.Run(jobSeconds, x.HasParam<CancellationToken>()), o => o.WithId("job")));
            })
            .Build();
    }

    private static async Task<T> Within<T>(Task<T> task, int seconds, string what)
    {
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)))).ShouldBeSameAs(task, $"timed out waiting for {what}");
        return await task;
    }

    private static Task<bool> Hang(CancellationToken _) => new TaskCompletionSource<bool>().Task; // ignores the token on purpose

    [Fact]
    public async Task A_Renewal_Outage_Abandons_The_Run_Before_The_Confirmed_Lease_Lapses()
    {
        var probe = new Probe();
        // Call 1 (the confirmation before the run) succeeds and fixes the confirmed expiry at start + 24s; after that
        // the store is unreachable. TTL 24s here (keepalive 12s, retry 3s): the abandonment point is reached after a
        // handful of timer waits, each of which can fire late on a busy CI runner, so the margin before the lease end
        // (LockTtl/8 = 3s) is deliberately generous to absorb their accumulated drift.
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) =>
            n == 1 ? inner.RenewLockAsync(id, owner, until, ct) : throw new TimeoutException("store unreachable"));
        using var host = BuildHost(store, probe, jobSeconds: 40, o => o.LockTtl = TimeSpan.FromSeconds(24));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            var started = await Within(probe.Started.Task, 5, "the run to start");
            var cancelled = await Within(probe.Cancelled.Task, 35, "the run to be cancelled");
            var lost = await Within(probe.LockLost.Task, 5, "OnLockLost");

            var elapsed = cancelled - started;
            // Not on the first failed renewal (at ~12s): it retried while the lease was still confirmed …
            elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(13), "a single failed renewal must not abandon the run");
            store.Calls.ShouldBeGreaterThanOrEqualTo(3, "the renewal should have been retried");
            // … but before the expiry the store last confirmed, so no other instance can overlap with it. (Asserted
            // against the lease the store actually handed out, not a guessed number.)
            cancelled.ShouldBeLessThan(store.ConfirmedUntil!.Value, "the run must stop before the confirmed lease can lapse");
            lost.ExecutionId.ShouldNotBeNullOrEmpty();
            probe.Successes.ShouldBe(0);

            // The abandoning worker finalizes its own history row so nothing lingers as Running. (The task itself
            // is re-run afterwards — the claim is released and the run never completed — so more rows may follow.)
            await Task.Delay(300);
            var row = (await store.GetExecutionsAsync("job", 10)).Single(e => e.Id == lost.ExecutionId);
            row.Status.ShouldBe(JobExecutionStatus.Cancelled);
            row.Error.ShouldBe(CronnerExecutionErrors.LockLost);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Single_Failed_Renewal_Does_Not_Abandon_The_Run()
    {
        var probe = new Probe();
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) =>
            n == 2 ? throw new TimeoutException("blip") : inner.RenewLockAsync(id, owner, until, ct));
        using var host = BuildHost(store, probe, jobSeconds: 6);   // renewal at 4s (throws), retry at 5s (ok), done at 6s

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(probe.Completed.Task, 20, "the run to complete");
            await Task.Delay(300);

            probe.Successes.ShouldBe(1);
            probe.LockLost.Task.IsCompleted.ShouldBeFalse("a transient blip is not a lost lock");
            probe.KeepAlives.ShouldBeGreaterThanOrEqualTo(1, "later renewals succeeded and fired the keepalive hook");
            (await store.GetExecutionsAsync("job", 10)).ShouldHaveSingleItem().Status.ShouldBe(JobExecutionStatus.Succeeded);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Hanging_Renewal_Counts_As_Unconfirmed_And_The_Run_Still_Stops_In_Time()
    {
        var probe = new Probe();
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) =>
            n == 1 ? inner.RenewLockAsync(id, owner, until, ct) : Hang(ct));
        // TTL 24s (margin LockTtl/8 = 3s) so a busy CI runner's timer drift can't push the observed cancel past
        // the confirmed lease — the store's decision is right, only the wall-clock measurement is jittery.
        using var host = BuildHost(store, probe, jobSeconds: 40, o => o.LockTtl = TimeSpan.FromSeconds(24));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            var started = await Within(probe.Started.Task, 5, "the run to start");
            var cancelled = await Within(probe.Cancelled.Task, 35, "the run to be cancelled");
            await Within(probe.LockLost.Task, 5, "OnLockLost");

            cancelled.ShouldBeLessThan(store.ConfirmedUntil!.Value, "a hung store call must not let the lease lapse silently");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Renewal_Answered_False_Is_An_Immediate_Loss()
    {
        var probe = new Probe();
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) =>
            n == 1 ? inner.RenewLockAsync(id, owner, until, ct) : Task.FromResult(false));
        using var host = BuildHost(store, probe, jobSeconds: 30);

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            var started = await Within(probe.Started.Task, 5, "the run to start");
            var cancelled = await Within(probe.Cancelled.Task, 20, "the run to be cancelled");
            await Within(probe.LockLost.Task, 5, "OnLockLost");

            // The first renewal is at ~4s; a definitive "no" cancels right there — no retries (exactly the confirmation
            // and the one refused renewal), long before the confirmed lease would have lapsed.
            store.Calls.ShouldBe(2);
            cancelled.ShouldBeLessThan(store.ConfirmedUntil!.Value);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Claim_The_Store_No_Longer_Confirms_At_Run_Start_Is_Not_Run()
    {
        var probe = new Probe();
        var started = 0;
        // The store denies the very first renewal — the one the engine uses to confirm the claim before running.
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) => Task.FromResult(false));
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<ForwardingStore>(store);
                services.AddDotnetCronner(cronner => cronner
                    .UseStore<ForwardingStore>()
                    .Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); })
                    .OnStart(_ => { Interlocked.Increment(ref started); return Task.CompletedTask; })
                    .OnLockLost(ctx => { probe.LockLost.TrySetResult(ctx); return Task.CompletedTask; })
                    .Sched<LongJob>(x => x.Run(1, x.HasParam<CancellationToken>()), o => o.WithId("job")));
            })
            .Build();

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Task.Delay(1500);

            probe.Started.Task.IsCompleted.ShouldBeFalse("a task whose claim is not ours must not run");
            started.ShouldBe(0, "no lifecycle hook fires for a run that never starts");
            probe.LockLost.Task.IsCompleted.ShouldBeFalse("nothing was lost — nothing had started");
            store.Calls.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Cancel_Written_To_The_Store_Stops_The_Run_As_A_Cancel_Not_A_Lost_Lock()
    {
        // The in-memory store refuses to renew a Cancelled job; the engine must classify that as a cancel.
        var probe = new Probe();
        var store = new ScriptedRenewStore((n, inner, id, owner, until, ct) => inner.RenewLockAsync(id, owner, until, ct));
        using var host = BuildHost(store, probe, jobSeconds: 30);

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(probe.Started.Task, 5, "the run to start");

            // Simulate ICronnerClient.CancelTaskAsync on ANOTHER instance: it only sees the store.
            var copy = (await store.GetByIdAsync("job"))!;
            copy.State = CronnerTaskState.Cancelled;
            copy.NextRunUtc = null;
            await store.UpsertAsync(copy);

            await Within(probe.Cancelled.Task, 20, "the run to be cancelled");
            await Task.Delay(500);

            probe.Cancels.ShouldBe(1);
            probe.LockLost.Task.IsCompleted.ShouldBeFalse("a cancel is not a lost lock");
            var job = (await store.GetByIdAsync("job"))!;
            job.State.ShouldBe(CronnerTaskState.Cancelled);
            job.LockOwner.ShouldBeNull();
            (await store.GetExecutionsAsync("job", 10)).ShouldHaveSingleItem().Status.ShouldBe(JobExecutionStatus.Cancelled);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
