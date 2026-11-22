using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests.Shared;

/// <summary>
/// The scheduler's everyday behaviour, run end to end — a real host, a real poll loop — against every backend:
/// a run and its persisted outcome, retries, cancellation, one-off jobs with a payload, both kinds of retention,
/// misfire catch-up, and what survives a restart or a crashed instance. <see cref="SchedulerExclusivityTests"/>
/// covers the two-instance guarantees; this suite is the reason a feature that works on the in-memory store can
/// be expected to work on PostgreSQL, SQL Server and Redis too.
/// </summary>
public abstract class SchedulerScenarioTests : IAsyncLifetime
{
    protected IStoreBackend Backend { get; private set; } = default!;

    protected abstract Task<IStoreBackend> CreateBackendAsync();

    public async Task InitializeAsync() => Backend = await CreateBackendAsync();

    public Task DisposeAsync() => Backend.DisposeAsync().AsTask();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------------------------------------------------
    // Jobs
    // ---------------------------------------------------------------------------------------------------------

    public sealed record Run(string TaskId, string ExecutionId, int Attempt, string? Payload);

    public sealed class Log
    {
        private readonly ConcurrentQueue<Run> _runs = new();

        public IReadOnlyList<Run> Runs => _runs.ToArray();

        public int Count => _runs.Count;

        public TaskCompletionSource Blocking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(Run run) => _runs.Enqueue(run);
    }

    public sealed record ImportPayload(int BatchSize, string Source);

    public sealed class Jobs(Log log)
    {
        public Task Succeed(string taskId, ICronnerJobContext ctx, CancellationToken ct)
        {
            log.Add(new Run(taskId, ctx.ExecutionId, ctx.Attempt, null));
            return Task.CompletedTask;
        }

        public Task Fail(string taskId, ICronnerJobContext ctx, CancellationToken ct)
        {
            log.Add(new Run(taskId, ctx.ExecutionId, ctx.Attempt, null));
            throw new InvalidOperationException($"attempt {ctx.Attempt} failed");
        }

        public Task Import(ImportPayload payload, ICronnerJobContext ctx, CancellationToken ct)
        {
            log.Add(new Run("import", ctx.ExecutionId, ctx.Attempt, $"{payload.Source}:{payload.BatchSize}"));
            return Task.CompletedTask;
        }

        public async Task Block(string taskId, ICronnerJobContext ctx, CancellationToken ct)
        {
            log.Add(new Run(taskId, ctx.ExecutionId, ctx.Attempt, null));
            log.Blocking.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------------------

    private IHost BuildHost(Log log, Action<ICronnerBuilder> configure)
    {
        var store = new ForwardingStore(Backend.CreateStore());
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(store);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.UseStore<ForwardingStore>();
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

    private static void Succeeding(ICronnerBuilder cronner, string id) =>
        cronner.Sched<Jobs>(
            x => x.Succeed(id, x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
            o => o.WithId(id));

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"{Backend.Name}: timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    /// <summary>Waits until the job has finished <paramref name="runs"/> runs and its lock is released again.</summary>
    private async Task<CronnerJob> WaitForRunsAsync(ICronnerStore store, string id, int runs)
    {
        CronnerJob? job = null;
        await WaitUntilAsync(
            async () => (job = await store.GetByIdAsync(id)) is { LockOwner: null } current && current.RunCount >= runs,
            $"'{id}' to finish run {runs} and release its lock");
        return job!;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Normal execution
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task A_Triggered_Task_Runs_Once_And_Its_Outcome_Is_Persisted()
    {
        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithExecutionHistory(10);
            Succeeding(cronner, "job");
        });
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();
            var before = DateTimeOffset.UtcNow;

            await client.TriggerNowAsync("job");
            var job = await WaitForRunsAsync(store, "job", 1);
            await Task.Delay(300); // a duplicate run would show up here

            log.Runs.ShouldHaveSingleItem().TaskId.ShouldBe("job");
            job.RunCount.ShouldBe(1);
            job.State.ShouldBe(CronnerTaskState.Completed);
            job.NextRunUtc.ShouldBeNull("a task without a cron expression is not rescheduled");
            job.LastRunUtc.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
            job.LastError.ShouldBeNull();
            job.RetryCount.ShouldBe(0);
            job.LockedUntilUtc.ShouldBeNull();

            var execution = (await client.GetExecutionsAsync("job")).ShouldHaveSingleItem();
            execution.Id.ShouldBe(log.Runs[0].ExecutionId, "history is keyed by the execution id the job saw");
            execution.Status.ShouldBe(JobExecutionStatus.Succeeded);
            execution.Attempt.ShouldBe(1);
            execution.FinishedAt.ShouldNotBeNull();
            execution.Error.ShouldBeNull();
            execution.Owner.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Retries
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task A_Failing_Task_Is_Retried_Up_To_Its_Limit_And_Ends_Failed()
    {
        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithExecutionHistory(10);
            cronner.Sched<Jobs>(
                x => x.Fail("flaky", x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
                o => o.WithId("flaky").WithRetryPolicy(p => p.MaxAttempts(3).Immediate()));
        });
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            await client.TriggerNowAsync("flaky");
            CronnerJob? job = null;
            await WaitUntilAsync(
                async () => (job = await store.GetByIdAsync("flaky")) is { State: CronnerTaskState.Failed, LockOwner: null },
                "the task to exhaust its attempts");
            await Task.Delay(300); // a fourth attempt would show up here

            // The attempt number travels through the store (RetryCount) between runs, so this also proves that
            // the retry state round-trips on this backend.
            log.Runs.Select(r => r.Attempt).ShouldBe([1, 2, 3]);
            log.Runs.Select(r => r.ExecutionId).Distinct().Count().ShouldBe(3, "every attempt is its own execution");
            job!.RunCount.ShouldBe(3);
            job.RetryCount.ShouldBe(0, "the retry counter is reset once the attempts are spent");
            job.NextRunUtc.ShouldBeNull();
            job.LastError.ShouldBe("attempt 3 failed");

            var history = await client.GetExecutionsAsync("flaky");
            history.Count.ShouldBe(3);
            history.ShouldAllBe(e => e.Status == JobExecutionStatus.Failed);
            history.Select(e => e.Attempt).OrderBy(a => a).ShouldBe([1, 2, 3]);
            history.Single(e => e.Attempt == 3).Error.ShouldNotBeNull().ShouldContain("attempt 3 failed");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Cancellation
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Cancelling_A_Running_Task_Stops_It_And_Records_The_Cancellation()
    {
        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithExecutionHistory(10);
            cronner.Sched<Jobs>(
                x => x.Block("long", x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
                o => o.WithId("long"));
        });
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            await client.TriggerNowAsync("long");
            await log.Blocking.Task.WaitAsync(Timeout);
            (await store.GetByIdAsync("long"))!.LockOwner.ShouldNotBeNull("a running task holds its lock");

            await client.CancelTaskAsync("long");
            CronnerJob? job = null;
            await WaitUntilAsync(
                async () => (job = await store.GetByIdAsync("long")) is { State: CronnerTaskState.Cancelled, LockOwner: null },
                "the cancelled run to be finalized");

            job!.NextRunUtc.ShouldBeNull();
            var execution = (await client.GetExecutionsAsync("long")).ShouldHaveSingleItem();
            execution.Status.ShouldBe(JobExecutionStatus.Cancelled);
            execution.FinishedAt.ShouldNotBeNull();

            // A cancelled task stays out of the schedule until it is triggered again.
            (await store.AcquireDueAsync(DateTimeOffset.UtcNow.AddDays(1), "probe", TimeSpan.FromSeconds(1), max: 10)).ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // One-off jobs
    // ---------------------------------------------------------------------------------------------------------

    private static void Importing(ICronnerBuilder cronner) =>
        cronner.Sched<Jobs>(
            x => x.Import(x.HasParam<ImportPayload>(), x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
            o => o.WithId("import"));

    [ContractFact]
    public async Task An_Enqueued_One_Off_Runs_Once_With_Its_Payload_And_A_Future_One_Waits()
    {
        var log = new Log();
        using var host = BuildHost(log, Importing);
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            var later = await client.EnqueueAsync("import", new ImportPayload(10, "later"), runAt: DateTimeOffset.UtcNow.AddHours(1));
            var now = await client.EnqueueAsync("import", new ImportPayload(500, "now"), CronnerTaskPriority.High);
            now.ShouldNotBe(later);

            var done = await WaitForRunsAsync(store, now, 1);
            await Task.Delay(300); // the future instance (or a second run) would show up here

            log.Runs.ShouldHaveSingleItem().Payload.ShouldBe("now:500", $"{Backend.Name}: the payload must survive the store");
            done.Kind.ShouldBe(CronnerJobKind.OneOff);
            done.DefinitionId.ShouldBe("import");
            done.State.ShouldBe(CronnerTaskState.Completed);
            done.Priority.ShouldBe(CronnerTaskPriority.High);
            done.NextRunUtc.ShouldBeNull("a one-off never reschedules");

            var waiting = (await store.GetByIdAsync(later)).ShouldNotBeNull();
            waiting.State.ShouldBe(CronnerTaskState.Scheduled);
            waiting.RunCount.ShouldBe(0);
            waiting.NextRunUtc.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(30));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [ContractFact]
    public async Task One_Off_Retention_Keeps_Only_The_Newest_Finished_Instances()
    {
        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithOneOffRetention(2);
            Importing(cronner);
        });
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            var ids = new List<string>();
            for (var i = 1; i <= 5; i++)
            {
                // One at a time, so "newest" is unambiguous.
                ids.Add(await client.EnqueueAsync("import", new ImportPayload(i, "batch")));
                await WaitUntilAsync(() => Task.FromResult(log.Count == i), $"one-off {i} to run");
            }

            IReadOnlyList<CronnerJob> remaining = [];
            await WaitUntilAsync(async () =>
            {
                remaining = (await store.GetAsync(null, 0, 100)).Where(j => j.Kind == CronnerJobKind.OneOff).ToArray();
                return remaining.Count == 2 && remaining.All(j => j.LockOwner is null);
            }, "retention to prune down to two instances");

            remaining.Select(j => j.Id).OrderBy(x => x).ShouldBe(ids.Skip(3).OrderBy(x => x));
            log.Runs.Select(r => r.Payload).ShouldBe(["batch:1", "batch:2", "batch:3", "batch:4", "batch:5"]);
            // The definition row is not an instance and is never pruned.
            (await store.GetByIdAsync("import")).ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Execution history retention
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task History_Retention_Keeps_Only_The_Newest_Runs_Of_Each_Task()
    {
        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithExecutionHistory(3);
            Succeeding(cronner, "busy");
            Succeeding(cronner, "quiet");
        });
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            await client.TriggerNowAsync("quiet");
            await WaitForRunsAsync(store, "quiet", 1);
            for (var run = 1; run <= 5; run++)
            {
                await client.TriggerNowAsync("busy");
                await WaitForRunsAsync(store, "busy", run);
            }

            IReadOnlyList<CronnerJobExecution> history = [];
            await WaitUntilAsync(
                async () => (history = await client.GetExecutionsAsync("busy")).Count == 3,
                "history to be pruned down to three rows");

            var newest = log.Runs.Where(r => r.TaskId == "busy").Select(r => r.ExecutionId).Reverse().Take(3);
            history.Select(e => e.Id).ShouldBe(newest, "the newest three runs are kept, newest first");
            (await client.GetExecutionsAsync("quiet")).ShouldHaveSingleItem();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Restart and crash recovery
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Task_State_Survives_A_Restart()
    {
        if (!Backend.SupportsMultipleInstances)
            return; // the in-memory store lives and dies with its process; there is nothing to survive into

        var log = new Log();
        using (var first = BuildHost(log, cronner => Succeeding(cronner, "job")))
        {
            await first.StartAsync();
            var client = first.Services.GetRequiredService<ICronnerClient>();
            await client.TriggerNowAsync("job");
            await WaitForRunsAsync(Backend.CreateStore(), "job", 1);
            await client.TriggerNowAsync("job");
            await WaitForRunsAsync(Backend.CreateStore(), "job", 2);
            await first.StopAsync();
        }

        using var second = BuildHost(log, cronner => Succeeding(cronner, "job"));
        await second.StartAsync();
        try
        {
            var client = second.Services.GetRequiredService<ICronnerClient>();
            var store = Backend.CreateStore();

            // Seeding at start-up must adopt the existing row, not reset it.
            var adopted = (await client.GetTaskByIdAsync("job")).ShouldNotBeNull();
            adopted.RunCount.ShouldBe(2, $"{Backend.Name}: start-up seeding reset a task that already had history");
            adopted.LastRunUtc.ShouldNotBeNull();

            await client.TriggerNowAsync("job");
            (await WaitForRunsAsync(store, "job", 3)).RunCount.ShouldBe(3);
            log.Count.ShouldBe(3);
        }
        finally
        {
            await second.StopAsync();
        }
    }

    [ContractFact]
    public async Task A_Run_Abandoned_By_A_Crashed_Instance_Is_Picked_Up_Once_Its_Lease_Lapses()
    {
        // The state a crash leaves behind: the task claimed by an owner that no longer exists, and a history row
        // still saying Running.
        var seed = Backend.CreateStore();
        await seed.UpsertAsync(new CronnerJob
        {
            Id = "job",
            Name = "job",
            NextRunUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            State = CronnerTaskState.Scheduled,
        });
        var lease = TimeSpan.FromSeconds(2);
        (await seed.AcquireDueAsync(DateTimeOffset.UtcNow, "crashed-instance", lease, max: 1)).ShouldHaveSingleItem();
        await seed.RecordExecutionStartedAsync(new CronnerJobExecution
        {
            Id = "abandoned-run",
            JobId = "job",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            Status = JobExecutionStatus.Running,
            Attempt = 1,
            Owner = "crashed-instance",
        });

        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.WithExecutionHistory(10);
            Succeeding(cronner, "job");
        });
        var started = DateTimeOffset.UtcNow;
        await host.StartAsync();
        try
        {
            var job = await WaitForRunsAsync(Backend.CreateStore(), "job", 1);

            log.Runs.ShouldHaveSingleItem();
            (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(lease - TimeSpan.FromSeconds(1),
                $"{Backend.Name}: the task was taken over while the crashed instance's lease was still live");
            job.State.ShouldBe(CronnerTaskState.Completed);

            var history = await host.Services.GetRequiredService<ICronnerClient>().GetExecutionsAsync("job");
            history.Count.ShouldBe(2);
            var orphan = history.Single(e => e.Id == "abandoned-run");
            orphan.Status.ShouldBe(JobExecutionStatus.Failed, "the abandoned run must not stay Running forever");
            orphan.Error.ShouldBe(CronnerExecutionErrors.Orphaned);
            orphan.FinishedAt.ShouldNotBeNull();
            history.Single(e => e.Id != "abandoned-run").Status.ShouldBe(JobExecutionStatus.Succeeded);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Misfires
    // ---------------------------------------------------------------------------------------------------------

    // Hourly, with the minute pinned ~30 minutes away from now so that no regular occurrence can fire while a test
    // runs (see MisfireTests for the long version of why minute 0 would be flaky near the top of the hour).
    private static readonly string HourlyCron = $"{(DateTimeOffset.UtcNow.Minute + 30) % 60} * * * *";

    private async Task<int> RunsAfterAnOutageAsync(MisfirePolicy policy, int catchUpMax)
    {
        // The store as an outage leaves it: the task's next occurrence is five hours in the past.
        await Backend.CreateStore().UpsertAsync(new CronnerJob
        {
            Id = "tick",
            Name = "tick",
            CronExpression = HourlyCron,
            NextRunUtc = DateTimeOffset.UtcNow.AddHours(-5),
            State = CronnerTaskState.Scheduled,
        });

        var log = new Log();
        using var host = BuildHost(log, cronner =>
        {
            cronner.Configure(o =>
            {
                o.MisfireThreshold = TimeSpan.FromMilliseconds(500);
                o.MisfireCatchUpMax = catchUpMax;
            });
            cronner.Sched<Jobs>(
                x => x.Succeed("tick", x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
                o => o.WithCron(HourlyCron).WithId("tick").WithMisfirePolicy(policy));
        });
        await host.StartAsync();
        try
        {
            var store = Backend.CreateStore();
            await WaitUntilAsync(
                async () => await store.GetByIdAsync("tick") is { LockOwner: null, NextRunUtc: { } next } &&
                            next > DateTimeOffset.UtcNow.AddMinutes(1),
                "the backlog to be resolved and the schedule to point at a future occurrence");
            await Task.Delay(300); // a straggling catch-up run would show up here
            return log.Count;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [ContractFact]
    public async Task After_An_Outage_FireOnce_Runs_A_Single_Catch_Up()
    {
        (await RunsAfterAnOutageAsync(MisfirePolicy.FireOnce, catchUpMax: 100)).ShouldBe(1);
    }

    [ContractFact]
    public async Task After_An_Outage_Skip_Runs_Nothing()
    {
        (await RunsAfterAnOutageAsync(MisfirePolicy.Skip, catchUpMax: 100)).ShouldBe(0);
    }

    [ContractFact]
    public async Task After_An_Outage_FireAll_Drains_The_Backlog_Up_To_The_Cap()
    {
        // Five occurrences were missed; the cap keeps the most recent three. Each catch-up is a separate claim, so
        // this walks the backlog through the store three times.
        (await RunsAfterAnOutageAsync(MisfirePolicy.FireAll, catchUpMax: 3)).ShouldBe(3);
    }
}
