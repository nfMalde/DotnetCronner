using Shouldly;

namespace DotnetCronner.Tests.Shared;

/// <summary>
/// The part of the <see cref="ICronnerStore"/> contract that is about <em>data</em> rather than the execution lock
/// (that half lives in <see cref="StoreLockContractTests"/>): what a job looks like after a round trip, how reads
/// page and filter, which rows a claim may and may not pick, and how execution history and the two retention
/// operations behave. Every shipped store must give the same answers, because the engine and
/// <see cref="ICronnerClient"/> are written against the interface and never know which store is behind it.
/// </summary>
public abstract class StoreDataContractTests : IAsyncLifetime
{
    protected IStoreBackend Backend { get; private set; } = default!;

    protected abstract Task<IStoreBackend> CreateBackendAsync();

    public async Task InitializeAsync() => Backend = await CreateBackendAsync();

    public Task DisposeAsync() => Backend.DisposeAsync().AsTask();

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    // Backends differ in timestamp resolution (PostgreSQL keeps microseconds, .NET ticks are 100 ns). Seeding whole
    // milliseconds keeps a round trip exact everywhere; comparisons still allow a millisecond of slack.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1);

    private static DateTimeOffset Ms(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, value.Offset);

    private static DateTimeOffset Now => Ms(DateTimeOffset.UtcNow);

    private static CronnerJob NewJob(string id, DateTimeOffset? due = null, DateTimeOffset? created = null)
    {
        var now = Now;
        return new CronnerJob
        {
            Id = id,
            Name = id,
            CronExpression = "* * * * *",
            NextRunUtc = due,
            State = due is null ? CronnerTaskState.Idle : CronnerTaskState.Scheduled,
            CreatedUtc = created ?? now,
            UpdatedUtc = created ?? now,
        };
    }

    private static CronnerJobExecution NewExecution(string id, string jobId, DateTimeOffset startedAt) => new()
    {
        Id = id,
        JobId = jobId,
        StartedAt = startedAt,
        Status = JobExecutionStatus.Running,
        Attempt = 1,
        Owner = "owner",
    };

    // ---------------------------------------------------------------------------------------------------------
    // Reachability
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Ping_Answers_On_A_Reachable_Store()
    {
        await Should.NotThrowAsync(() => Backend.CreateStore().PingAsync());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Round trip
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task GetById_Of_An_Unknown_Id_Is_Null()
    {
        (await Backend.CreateStore().GetByIdAsync("nope")).ShouldBeNull();
    }

    [ContractFact]
    public async Task Upsert_Then_GetById_Round_Trips_Every_Field()
    {
        var store = Backend.CreateStore();
        var created = Now.AddDays(-2);
        var job = new CronnerJob
        {
            Id = "import#0001",
            Name = "Import — nightly ✓",
            Kind = CronnerJobKind.OneOff,
            DefinitionId = "import",
            Payload = "{\"batchSize\":500,\"source\":\"a \\\"quoted\\\" value\"}",
            PayloadType = "Sample.ImportPayload",
            Progress = 42.5m,
            CronExpression = null,
            State = CronnerTaskState.Failed,
            Priority = CronnerTaskPriority.High,
            NextRunUtc = Now.AddMinutes(5),
            LastRunUtc = Now.AddMinutes(-5),
            RunCount = 7,
            RetryCount = 2,
            LastError = "System.InvalidOperationException: boom\n   at Somewhere()",
            CreatedUtc = created,
            UpdatedUtc = created,
        };
        var before = DateTimeOffset.UtcNow;

        await store.UpsertAsync(job.Clone());

        // Read through a second instance: nothing may live only in the writer's memory.
        var read = (await Backend.CreateStore().GetByIdAsync(job.Id)).ShouldNotBeNull();
        read.Id.ShouldBe(job.Id);
        read.Name.ShouldBe(job.Name);
        read.Kind.ShouldBe(CronnerJobKind.OneOff);
        read.DefinitionId.ShouldBe("import");
        read.Payload.ShouldBe(job.Payload);
        read.PayloadType.ShouldBe(job.PayloadType);
        read.Progress.ShouldBe(42.5m);
        read.CronExpression.ShouldBeNull();
        read.State.ShouldBe(CronnerTaskState.Failed);
        read.Priority.ShouldBe(CronnerTaskPriority.High);
        read.NextRunUtc.ShouldNotBeNull().ShouldBe(job.NextRunUtc!.Value, Slack);
        read.LastRunUtc.ShouldNotBeNull().ShouldBe(job.LastRunUtc!.Value, Slack);
        read.RunCount.ShouldBe(7);
        read.RetryCount.ShouldBe(2);
        read.LastError.ShouldBe(job.LastError);
        read.LockOwner.ShouldBeNull();
        read.LockedUntilUtc.ShouldBeNull();
        read.CreatedUtc.ShouldBe(created, Slack, $"{Backend.Name}: CreatedUtc is the caller's value, not the write time");
        read.UpdatedUtc.ShouldBeGreaterThanOrEqualTo(before - Slack, $"{Backend.Name}: UpdatedUtc is stamped by the store on every write");
    }

    [ContractFact]
    public async Task Upsert_Of_An_Existing_Id_Updates_The_Row_Instead_Of_Adding_One()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", Now.AddHours(1)));

        var copy = (await store.GetByIdAsync("job"))!;
        copy.Name = "renamed";
        copy.RunCount = 3;
        copy.State = CronnerTaskState.Completed;
        copy.NextRunUtc = null;
        copy.LastError = null;
        await store.UpsertAsync(copy);

        (await store.GetAsync(null, 0, 10)).ShouldHaveSingleItem();
        var read = (await store.GetByIdAsync("job"))!;
        read.Name.ShouldBe("renamed");
        read.RunCount.ShouldBe(3);
        read.State.ShouldBe(CronnerTaskState.Completed);
        read.NextRunUtc.ShouldBeNull($"{Backend.Name}: a cleared nullable field must be stored as cleared");
    }

    [ContractFact]
    public async Task A_Returned_Job_Is_A_Snapshot_That_Does_Not_Write_Through()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", Now.AddHours(1)));

        var first = (await store.GetByIdAsync("job"))!;
        first.Name = "mutated locally";
        first.State = CronnerTaskState.Cancelled;

        var second = (await store.GetByIdAsync("job"))!;
        second.Name.ShouldBe("job");
        second.State.ShouldBe(CronnerTaskState.Scheduled);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Listing
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Get_Orders_By_Creation_Time_And_Pages()
    {
        var store = Backend.CreateStore();
        var t0 = Now.AddHours(-1);
        // Inserted out of order on purpose: the order must come from CreatedUtc, not from insertion.
        foreach (var i in new[] { 3, 0, 4, 1, 2 })
            await store.UpsertAsync(NewJob($"job-{i}", created: t0.AddMinutes(i)));

        (await store.GetAsync(null, 0, 100)).Select(j => j.Id)
            .ShouldBe(["job-0", "job-1", "job-2", "job-3", "job-4"]);
        (await store.GetAsync(null, 0, 2)).Select(j => j.Id).ShouldBe(["job-0", "job-1"]);
        (await store.GetAsync(null, 2, 2)).Select(j => j.Id).ShouldBe(["job-2", "job-3"]);
        (await store.GetAsync(null, 4, 2)).Select(j => j.Id).ShouldBe(["job-4"]);
        (await store.GetAsync(null, 5, 2)).ShouldBeEmpty();
        (await store.GetAsync(null, 0, 0)).ShouldBeEmpty();
    }

    [ContractFact]
    public async Task Get_Filters_By_State_Before_Paging()
    {
        var store = Backend.CreateStore();
        var t0 = Now.AddHours(-1);
        var states = new[]
        {
            CronnerTaskState.Scheduled, CronnerTaskState.Failed, CronnerTaskState.Scheduled,
            CronnerTaskState.Completed, CronnerTaskState.Scheduled,
        };
        for (var i = 0; i < states.Length; i++)
        {
            var job = NewJob($"job-{i}", created: t0.AddMinutes(i));
            job.State = states[i];
            await store.UpsertAsync(job);
        }

        (await store.GetAsync(CronnerTaskState.Scheduled, 0, 100)).Select(j => j.Id).ShouldBe(["job-0", "job-2", "job-4"]);
        (await store.GetAsync(CronnerTaskState.Scheduled, 1, 1)).Select(j => j.Id).ShouldBe(["job-2"]);
        (await store.GetAsync(CronnerTaskState.Failed, 0, 100)).Select(j => j.Id).ShouldBe(["job-1"]);
        (await store.GetAsync(CronnerTaskState.Running, 0, 100)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Removal
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Remove_Deletes_The_Job_And_Is_A_No_Op_For_An_Unknown_Id()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("keep", Now.AddSeconds(-1)));
        await store.UpsertAsync(NewJob("gone", Now.AddSeconds(-1)));

        await store.RemoveAsync("gone");
        await Should.NotThrowAsync(() => store.RemoveAsync("gone"));
        await Should.NotThrowAsync(() => store.RemoveAsync("never-existed"));

        (await store.GetByIdAsync("gone")).ShouldBeNull();
        (await store.GetAsync(null, 0, 10)).ShouldHaveSingleItem().Id.ShouldBe("keep");
        // A removed job is not left behind in whatever index the claim reads from.
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 10)).ShouldHaveSingleItem().Id.ShouldBe("keep");
    }

    [ContractFact]
    public async Task Remove_Takes_The_Jobs_Execution_History_With_It()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("a"));
        await store.UpsertAsync(NewJob("b"));
        await store.RecordExecutionStartedAsync(NewExecution("a-1", "a", Now.AddMinutes(-2)));
        await store.RecordExecutionStartedAsync(NewExecution("b-1", "b", Now.AddMinutes(-2)));

        await store.RemoveAsync("a");

        (await store.GetExecutionsAsync("a", 10)).ShouldBeEmpty();
        (await store.GetExecutionsAsync("b", 10)).ShouldHaveSingleItem();

        // Re-creating a job under the same id starts with a clean history, not the previous incarnation's.
        await store.UpsertAsync(NewJob("a"));
        (await store.GetExecutionsAsync("a", 10)).ShouldBeEmpty();
    }

    [ContractFact]
    public async Task Remove_Of_A_Claimed_Job_Leaves_No_Lock_Behind()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", Now.AddSeconds(-1)));
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        await store.RemoveAsync("job");
        (await store.RenewLockAsync("job", "A", DateTimeOffset.UtcNow + Ttl)).ShouldBeFalse();

        // The id is free again: a new job under it can be claimed straight away, by anyone.
        await store.UpsertAsync(NewJob("job", Now.AddSeconds(-1)));
        (await Backend.CreateStore().AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1))
            .ShouldHaveSingleItem().LockOwner.ShouldBe("B");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Which rows a claim may pick
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Claim_Takes_Only_Due_Jobs()
    {
        var store = Backend.CreateStore();
        var now = Now;
        await store.UpsertAsync(NewJob("due", now.AddSeconds(-5)));
        await store.UpsertAsync(NewJob("due-exactly-now", now));
        await store.UpsertAsync(NewJob("future", now.AddMinutes(5)));
        await store.UpsertAsync(NewJob("unscheduled", due: null));

        var claimed = await store.AcquireDueAsync(now, "A", Ttl, max: 10);

        claimed.Select(j => j.Id).OrderBy(x => x).ShouldBe(["due", "due-exactly-now"]);
        foreach (var job in claimed)
        {
            job.State.ShouldBe(CronnerTaskState.Queued);
            job.LockOwner.ShouldBe("A");
            job.LockedUntilUtc.ShouldNotBeNull().ShouldBe(now + Ttl, TimeSpan.FromSeconds(1));
        }

        (await store.GetByIdAsync("future"))!.LockOwner.ShouldBeNull();
        (await store.GetByIdAsync("future"))!.State.ShouldBe(CronnerTaskState.Scheduled);
        (await store.GetByIdAsync("unscheduled"))!.LockOwner.ShouldBeNull();
    }

    [ContractFact]
    public async Task Claim_Never_Takes_A_Cancelled_Job_Even_When_It_Still_Carries_A_Due_Time()
    {
        var store = Backend.CreateStore();
        var cancelled = NewJob("cancelled", Now.AddSeconds(-5));
        cancelled.State = CronnerTaskState.Cancelled;
        await store.UpsertAsync(cancelled);

        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 10)).ShouldBeEmpty();

        // Un-cancelling (TriggerNowAsync on a cancelled task) makes it claimable again.
        var revived = (await store.GetByIdAsync("cancelled"))!;
        revived.State = CronnerTaskState.Scheduled;
        revived.NextRunUtc = Now.AddSeconds(-1);
        await store.UpsertAsync(revived);
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 10)).ShouldHaveSingleItem().Id.ShouldBe("cancelled");
    }

    [ContractFact]
    public async Task Claim_Respects_Max_And_A_Non_Positive_Max_Claims_Nothing()
    {
        var store = Backend.CreateStore();
        for (var i = 0; i < 5; i++)
            await store.UpsertAsync(NewJob($"job-{i}", Now.AddSeconds(-10 + i)));

        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 0)).ShouldBeEmpty();
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 2)).Count.ShouldBe(2);
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 10)).Count.ShouldBe(3);
        (await store.GetAsync(CronnerTaskState.Queued, 0, 10)).Count.ShouldBe(5);
    }

    [ContractFact]
    public async Task A_Job_Rescheduled_Into_The_Future_Is_Not_Claimed_Until_Then()
    {
        // What the engine does after every run: write the next occurrence and release. The released row must not be
        // claimable again before that occurrence, whatever index the store keeps for due-ness.
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", Now.AddSeconds(-1)));
        var claimed = (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        var next = Now.AddMinutes(10);
        claimed.State = CronnerTaskState.Scheduled;
        claimed.NextRunUtc = next;
        claimed.RunCount++;
        await store.UpsertAsync(claimed);
        (await store.ReleaseLockAsync("job", "A")).ShouldBeTrue();

        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1)).ShouldBeEmpty();
        (await store.AcquireDueAsync(next.AddSeconds(1), "B", Ttl, max: 1)).ShouldHaveSingleItem().RunCount.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Progress
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task UpdateProgress_Changes_Only_The_Progress()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", Now.AddSeconds(-1)));
        var claimed = (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        await store.UpdateProgressAsync("job", 37.5m);
        await Should.NotThrowAsync(() => store.UpdateProgressAsync("unknown", 10m));

        var read = (await Backend.CreateStore().GetByIdAsync("job"))!;
        read.Progress.ShouldBe(37.5m);
        read.State.ShouldBe(CronnerTaskState.Queued);
        read.LockOwner.ShouldBe("A", $"{Backend.Name}: a progress write must not touch the lock");
        read.LockedUntilUtc.ShouldNotBeNull().ShouldBe(claimed.LockedUntilUtc!.Value, TimeSpan.FromSeconds(1));
        read.NextRunUtc.ShouldNotBeNull().ShouldBe(claimed.NextRunUtc!.Value, Slack);
        (await store.GetByIdAsync("unknown")).ShouldBeNull("a progress write for an unknown id must not create a row");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Execution history
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task History_Start_Then_Finish_Is_One_Record()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job"));
        var started = Now.AddSeconds(-30);
        var execution = NewExecution("exec-1", "job", started);
        execution.Attempt = 2;
        execution.Owner = "instance-a";

        await store.RecordExecutionStartedAsync(execution);

        var running = (await store.GetExecutionsAsync("job", 10)).ShouldHaveSingleItem();
        running.Id.ShouldBe("exec-1");
        running.JobId.ShouldBe("job");
        running.Status.ShouldBe(JobExecutionStatus.Running);
        running.StartedAt.ShouldBe(started, Slack);
        running.FinishedAt.ShouldBeNull();
        running.Duration.ShouldBeNull();
        running.Attempt.ShouldBe(2);
        running.Owner.ShouldBe("instance-a");
        running.Error.ShouldBeNull();

        var finished = Now;
        execution.Status = JobExecutionStatus.Failed;
        execution.FinishedAt = finished;
        execution.Error = "System.TimeoutException: too slow";
        await store.RecordExecutionFinishedAsync(execution);

        var done = (await Backend.CreateStore().GetExecutionsAsync("job", 10)).ShouldHaveSingleItem();
        done.Id.ShouldBe("exec-1");
        done.Status.ShouldBe(JobExecutionStatus.Failed);
        done.StartedAt.ShouldBe(started, Slack);
        done.FinishedAt.ShouldNotBeNull().ShouldBe(finished, Slack);
        done.Duration.ShouldNotBeNull().ShouldBe(finished - started, Slack);
        done.Error.ShouldBe("System.TimeoutException: too slow");
        done.Attempt.ShouldBe(2);
        done.Owner.ShouldBe("instance-a");
    }

    [ContractFact]
    public async Task History_Finish_Without_A_Recorded_Start_Still_Keeps_The_Run()
    {
        // The start insert can fail (a store blip) or predate history being switched on; the outcome must not be lost.
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job"));
        var execution = NewExecution("exec-1", "job", Now.AddSeconds(-5));
        execution.Status = JobExecutionStatus.Succeeded;
        execution.FinishedAt = Now;

        await store.RecordExecutionFinishedAsync(execution);

        (await store.GetExecutionsAsync("job", 10)).ShouldHaveSingleItem().Status.ShouldBe(JobExecutionStatus.Succeeded);
    }

    [ContractFact]
    public async Task History_Reads_Newest_First_Per_Job_And_Honours_The_Limit()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("a"));
        await store.UpsertAsync(NewJob("b"));
        var t0 = Now.AddHours(-1);
        // Recorded out of order: the order must come from StartedAt.
        foreach (var i in new[] { 2, 0, 3, 1 })
            await store.RecordExecutionStartedAsync(NewExecution($"a-{i}", "a", t0.AddMinutes(i)));
        await store.RecordExecutionStartedAsync(NewExecution("b-0", "b", t0.AddMinutes(10)));

        (await store.GetExecutionsAsync("a", 10)).Select(e => e.Id).ShouldBe(["a-3", "a-2", "a-1", "a-0"]);
        (await store.GetExecutionsAsync("a", 2)).Select(e => e.Id).ShouldBe(["a-3", "a-2"]);
        (await store.GetExecutionsAsync("a", 0)).ShouldBeEmpty();
        (await store.GetExecutionsAsync("b", 10)).Select(e => e.Id).ShouldBe(["b-0"]);
        (await store.GetExecutionsAsync("unknown", 10)).ShouldBeEmpty();
    }

    [ContractFact]
    public async Task History_Prune_Keeps_The_Newest_Of_That_Job_Only()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("a"));
        await store.UpsertAsync(NewJob("b"));
        var t0 = Now.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            await store.RecordExecutionStartedAsync(NewExecution($"a-{i}", "a", t0.AddMinutes(i)));
            await store.RecordExecutionStartedAsync(NewExecution($"b-{i}", "b", t0.AddMinutes(i)));
        }

        await store.PruneExecutionsAsync("a", keepNewest: 2);

        (await store.GetExecutionsAsync("a", 10)).Select(e => e.Id).ShouldBe(["a-4", "a-3"]);
        (await store.GetExecutionsAsync("b", 10)).Count.ShouldBe(5, $"{Backend.Name}: pruning one job must not touch another's history");

        // Pruning to more than there is, and pruning an unknown job, change nothing.
        await store.PruneExecutionsAsync("a", keepNewest: 10);
        await Should.NotThrowAsync(() => store.PruneExecutionsAsync("unknown", keepNewest: 1));
        (await store.GetExecutionsAsync("a", 10)).Count.ShouldBe(2);

        await store.PruneExecutionsAsync("a", keepNewest: 0);
        (await store.GetExecutionsAsync("a", 10)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------
    // One-off retention
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task OneOff_Prune_Removes_Only_The_Oldest_Finished_Instances_Of_That_Definition()
    {
        var store = Backend.CreateStore();

        async Task SeedAsync(string id, CronnerJobKind kind, string? definition, CronnerTaskState state)
        {
            var job = NewJob(id);
            job.Kind = kind;
            job.DefinitionId = definition;
            job.CronExpression = kind == CronnerJobKind.OneOff ? null : job.CronExpression;
            job.State = state;
            job.NextRunUtc = state is CronnerTaskState.Scheduled ? Now.AddHours(1) : null;
            await store.UpsertAsync(job);
            // "Newest" is by last write; space the writes so the order is unambiguous on every backend.
            await Task.Delay(15);
        }

        await SeedAsync("import", CronnerJobKind.Recurring, null, CronnerTaskState.Completed);     // the definition itself
        await SeedAsync("import#1", CronnerJobKind.OneOff, "import", CronnerTaskState.Completed);  // oldest finished
        await SeedAsync("import#2", CronnerJobKind.OneOff, "import", CronnerTaskState.Failed);
        await SeedAsync("import#3", CronnerJobKind.OneOff, "import", CronnerTaskState.Completed);
        await SeedAsync("import#4", CronnerJobKind.OneOff, "import", CronnerTaskState.Completed);  // newest finished
        await SeedAsync("import#5", CronnerJobKind.OneOff, "import", CronnerTaskState.Scheduled);  // still pending
        await SeedAsync("import#6", CronnerJobKind.OneOff, "import", CronnerTaskState.Running);    // in flight
        await SeedAsync("export#1", CronnerJobKind.OneOff, "export", CronnerTaskState.Completed);  // another definition

        await store.PruneCompletedOneOffsAsync("import", keepNewest: 2);

        (await store.GetAsync(null, 0, 100)).Select(j => j.Id).OrderBy(x => x)
            .ShouldBe(["export#1", "import", "import#3", "import#4", "import#5", "import#6"]);

        await store.PruneCompletedOneOffsAsync("import", keepNewest: 0);
        (await store.GetAsync(null, 0, 100)).Select(j => j.Id).OrderBy(x => x)
            .ShouldBe(["export#1", "import", "import#5", "import#6"]);

        await Should.NotThrowAsync(() => store.PruneCompletedOneOffsAsync("unknown", keepNewest: 1));
    }

    [ContractFact]
    public async Task OneOff_Prune_Takes_The_Pruned_Instances_History_With_It()
    {
        var store = Backend.CreateStore();
        foreach (var id in new[] { "import#1", "import#2" })
        {
            var job = NewJob(id);
            job.Kind = CronnerJobKind.OneOff;
            job.DefinitionId = "import";
            job.CronExpression = null;
            job.State = CronnerTaskState.Completed;
            await store.UpsertAsync(job);
            await store.RecordExecutionStartedAsync(NewExecution($"{id}-run", id, Now));
            await Task.Delay(15);
        }

        await store.PruneCompletedOneOffsAsync("import", keepNewest: 1);

        (await store.GetByIdAsync("import#1")).ShouldBeNull();
        (await store.GetExecutionsAsync("import#1", 10)).ShouldBeEmpty();
        (await store.GetExecutionsAsync("import#2", 10)).ShouldHaveSingleItem();
    }
}
