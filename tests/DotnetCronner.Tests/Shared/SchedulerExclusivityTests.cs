using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests.Shared;

/// <summary>
/// Runs TWO scheduler hosts (two <see cref="CronnerHostedService"/> instances with distinct owner ids) against
/// ONE backend and asserts the guarantee the README makes: a due task runs exactly once, never concurrently —
/// under contention, when runs outlast the lock TTL, when an instance dies mid-run, and when a cancel comes from
/// the other instance. Shared between the unit project (in-memory, SQLite file) and the integration project
/// (PostgreSQL, SQL Server, Redis).
/// </summary>
public abstract class SchedulerExclusivityTests : IAsyncLifetime
{
    protected IStoreBackend Backend { get; private set; } = default!;

    protected abstract Task<IStoreBackend> CreateBackendAsync();

    public async Task InitializeAsync() => Backend = await CreateBackendAsync();

    public Task DisposeAsync() => Backend.DisposeAsync().AsTask();

    /// <summary>The name of the host a job runs in (registered per host).</summary>
    public sealed record HostName(string Value);

    /// <summary>A job that records its run (task, execution id, host, start/end) and sleeps for a while.</summary>
    public sealed class RecordingJob(RunLog log, HostName host)
    {
        public async Task Run(string taskId, int durationMs, ICronnerJobContext ctx, CancellationToken ct)
        {
            using var _ = log.Begin(taskId, ctx.ExecutionId, host.Value);
            await Task.Delay(durationMs, ct);
        }
    }

    protected static IHost BuildHost(
        string name, ForwardingStore store, RunLog log, Action<CronnerOptions> configure, Action<ICronnerBuilder> tasks)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(new HostName(name));
                services.AddSingleton<ForwardingStore>(store);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.UseStore<ForwardingStore>();
                    cronner.Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                        configure(o);
                    });
                    cronner.WithExecutionHistory(100);
                    tasks(cronner);
                });
            })
            .Build();
    }

    protected static string OwnerOf(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<CronnerHostedService>().Single().Owner;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(25);
        }
    }

    private static Action<ICronnerBuilder> ManualTasks(IEnumerable<string> ids, int durationMs) => cronner =>
    {
        foreach (var id in ids)
            cronner.Sched<RecordingJob>(
                x => x.Run(id, durationMs, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
                o => o.WithId(id));
    };

    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Two_Schedulers_On_One_Store_Run_Every_Due_Task_Exactly_Once_And_Never_Concurrently()
    {
        var ids = Enumerable.Range(1, 12).Select(i => $"task-{i:00}").ToArray();
        var log = new RunLog();
        Action<CronnerOptions> options = o => o.MaxConcurrentTasks = 3;

        using var a = BuildHost("A", new ForwardingStore(Backend.CreateStore()), log, options, ManualTasks(ids, 300));
        using var b = BuildHost("B", new ForwardingStore(Backend.CreateStore()), log, options, ManualTasks(ids, 300));
        await a.StartAsync();
        await b.StartAsync();
        try
        {
            const int rounds = 2;
            for (var round = 1; round <= rounds; round++)
            {
                // Make every task due at once; both poll loops race for them.
                var client = (round % 2 == 1 ? a : b).Services.GetRequiredService<ICronnerClient>();
                foreach (var id in ids)
                    await client.TriggerNowAsync(id);

                await WaitUntilAsync(() => log.Count >= ids.Length * round, TimeSpan.FromSeconds(30), $"round {round} to finish");
                // Give a hypothetical duplicate run a moment to show up before asserting.
                await Task.Delay(600);
            }

            log.Count.ShouldBe(ids.Length * rounds, $"{Backend.Name}: every task must run exactly once per round");
            foreach (var id in ids)
                log.Records.Count(r => r.TaskId == id).ShouldBe(rounds, $"{Backend.Name}: task {id} did not run exactly once per round");
            log.ShouldShowNoConcurrentRunsOfTheSameTask();
            log.Records.Select(r => r.Host).Distinct().Count().ShouldBe(2, $"{Backend.Name}: both instances should have taken work");

            // The history agrees: one Succeeded row per run, owned by the instance that ran it, keyed by the id the job saw.
            var owners = new Dictionary<string, string> { ["A"] = OwnerOf(a), ["B"] = OwnerOf(b) };
            var store = Backend.CreateStore();
            foreach (var id in ids)
            {
                var history = await store.GetExecutionsAsync(id, 10);
                history.Count.ShouldBe(rounds);
                foreach (var execution in history)
                {
                    execution.Status.ShouldBe(JobExecutionStatus.Succeeded);
                    var record = log.Records.Single(r => r.ExecutionId == execution.Id);
                    execution.Owner.ShouldBe(owners[record.Host]);
                }
            }
        }
        finally
        {
            await a.StopAsync();
            await b.StopAsync();
        }
    }

    [ContractFact]
    public async Task Claims_Never_Wait_In_A_Queue_Longer_Than_Their_Lease()
    {
        // One worker, a 2s lease, three tasks of 2.5s each made due at once. An engine that claims all three up
        // front would leave two of them waiting (without a heartbeat) past their lease — and re-claim and re-run
        // the third. The engine must only claim what it can start.
        var ids = new[] { "slow-1", "slow-2", "slow-3" };
        var log = new RunLog();
        using var host = BuildHost("A", new ForwardingStore(Backend.CreateStore()), log,
            o => { o.MaxConcurrentTasks = 1; o.LockTtl = TimeSpan.FromSeconds(2); },
            ManualTasks(ids, 2500));
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            foreach (var id in ids)
                await client.TriggerNowAsync(id);

            await WaitUntilAsync(() => log.Count >= ids.Length, TimeSpan.FromSeconds(30), "all three runs to finish");
            await Task.Delay(3000); // long enough for a re-claimed duplicate to have started and finished

            log.Count.ShouldBe(ids.Length, $"{Backend.Name}: a queued claim outlived its lease and the task ran twice");
            foreach (var id in ids)
                log.Records.Count(r => r.TaskId == id).ShouldBe(1);
            log.ShouldShowNoConcurrentRunsOfTheSameTask();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [ContractFact]
    public async Task An_Instance_That_Loses_Its_Store_Mid_Run_Is_Stopped_Before_The_Survivor_Reclaims()
    {
        // Simulated crash: once the run has started, the running instance's store goes dead (every call throws).
        // Its heartbeat can no longer confirm the lease → it cancels its run BEFORE the lease lapses → the other
        // instance reclaims the task after the lease and runs it → no overlap; the dead instance's history row
        // (left Running) is finalized as orphaned by the survivor.
        const string id = "long";
        var log = new RunLog();
        var lockLostOn = new List<string>();
        var storeA = new KillableStore(Backend.CreateStore());
        var storeB = new KillableStore(Backend.CreateStore());
        Action<CronnerOptions> options = o => o.LockTtl = TimeSpan.FromSeconds(2);
        Action<ICronnerBuilder> tasks = cronner =>
        {
            cronner.OnLockLost(ctx => { lock (lockLostOn) lockLostOn.Add(ctx.HasParam<HostName>().Value); return Task.CompletedTask; });
            ManualTasks([id], 4000)(cronner);
        };

        using var a = BuildHost("A", storeA, log, options, tasks);
        using var b = BuildHost("B", storeB, log, options, tasks);
        await a.StartAsync();
        await b.StartAsync();
        try
        {
            await b.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync(id);
            await WaitUntilAsync(() => log.Started >= 1, TimeSpan.FromSeconds(10), "the first run to start");

            // Kill the store of whichever instance is running it.
            await Task.Delay(200);
            var runner = log.Records.Count == 0 ? RunningHost() : log.Records.First().Host;
            (runner == "A" ? storeA : storeB).Kill();

            await WaitUntilAsync(() => log.Count >= 2, TimeSpan.FromSeconds(30), "the reclaimed run to finish");

            var runs = log.Records.OrderBy(r => r.Start).ToArray();
            runs.Length.ShouldBe(2);
            runs[0].Host.ShouldBe(runner);
            runs[1].Host.ShouldNotBe(runner, "the survivor must have run the task");
            runs[0].End.ShouldBeLessThanOrEqualTo(runs[1].Start, $"{Backend.Name}: the dying instance's run overlapped the survivor's");
            log.ShouldShowNoConcurrentRunsOfTheSameTask();
            lockLostOn.ShouldBe([runner]);

            // History, read through the survivor's store: the dead run is orphaned → Failed, the survivor's Succeeded.
            // (The row is finalized right after the job body returns; give the engine that moment.)
            var survivorStore = runner == "A" ? storeB : storeA;
            IReadOnlyList<CronnerJobExecution> rows = [];
            await WaitUntilAsync(
                () => (rows = survivorStore.GetExecutionsAsync(id, 10).GetAwaiter().GetResult()).All(e => e.Status != JobExecutionStatus.Running),
                TimeSpan.FromSeconds(10), "both history rows to be finalized");
            var history = rows.ToDictionary(e => e.Id);
            history.Count.ShouldBe(2);
            var dead = history[runs[0].ExecutionId];
            dead.Status.ShouldBe(JobExecutionStatus.Failed);
            dead.Error.ShouldBe(CronnerExecutionErrors.Orphaned);
            dead.FinishedAt.ShouldNotBeNull();
            var survivor = history[runs[1].ExecutionId];
            survivor.Status.ShouldBe(JobExecutionStatus.Succeeded);
            survivor.Owner.ShouldBe(OwnerOf(runner == "A" ? b : a));
        }
        finally
        {
            await a.StopAsync();
            await b.StopAsync();
        }

        string RunningHost()
        {
            // Started but not finished: the job's Begin() ran, its record is not in the bag yet; find out via the
            // store instead — the Owner of the Running history row tells us which host claimed it.
            var store = storeA.IsDead ? storeB : storeA;
            var running = store.GetExecutionsAsync(id, 10).GetAwaiter().GetResult().First(e => e.Status == JobExecutionStatus.Running);
            return running.Owner == OwnerOf(a) ? "A" : "B";
        }
    }

    [ContractFact]
    public async Task A_Cancel_From_The_Other_Instance_Stops_The_Run_At_Its_Next_Keepalive()
    {
        const string id = "cancel-me";
        var log = new RunLog();
        var cancelledOn = new List<string>();
        var lockLost = 0;
        Action<CronnerOptions> options = o => o.LockTtl = TimeSpan.FromSeconds(2);
        Action<ICronnerBuilder> tasks = cronner =>
        {
            cronner.OnCancel(ctx => { lock (cancelledOn) cancelledOn.Add(ctx.HasParam<HostName>().Value); return Task.CompletedTask; });
            cronner.OnLockLost(_ => { Interlocked.Increment(ref lockLost); return Task.CompletedTask; });
            ManualTasks([id], 20000)(cronner);
        };

        using var a = BuildHost("A", new ForwardingStore(Backend.CreateStore()), log, options, tasks);
        using var b = BuildHost("B", new ForwardingStore(Backend.CreateStore()), log, options, tasks);
        await a.StartAsync();
        await b.StartAsync();
        try
        {
            await a.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync(id);
            await WaitUntilAsync(() => log.Started >= 1, TimeSpan.FromSeconds(10), "the run to start");
            await Task.Delay(200);

            // Find the instance that is NOT running it and cancel from there.
            var store = Backend.CreateStore();
            var running = (await store.GetExecutionsAsync(id, 10)).First(e => e.Status == JobExecutionStatus.Running);
            var other = running.Owner == OwnerOf(a) ? b : a;
            var runnerName = running.Owner == OwnerOf(a) ? "A" : "B";
            await other.Services.GetRequiredService<ICronnerClient>().CancelTaskAsync(id);

            await WaitUntilAsync(() => log.Count >= 1, TimeSpan.FromSeconds(10), "the run to stop");
            log.Records.Single().End.ShouldBeLessThan(log.Records.Single().Start.AddSeconds(10), "the run should stop at the next keepalive, not run to completion");
            await Task.Delay(500);

            cancelledOn.ShouldBe([runnerName]);
            lockLost.ShouldBe(0, "a cancel is a cancel, not a lost lock");
            var job = (await store.GetByIdAsync(id))!;
            job.State.ShouldBe(CronnerTaskState.Cancelled);
            job.NextRunUtc.ShouldBeNull();
            job.LockOwner.ShouldBeNull("the runner released its lock after the cancelled run");
            (await store.GetExecutionsAsync(id, 10)).Single().Status.ShouldBe(JobExecutionStatus.Cancelled);
        }
        finally
        {
            await a.StopAsync();
            await b.StopAsync();
        }
    }
}
