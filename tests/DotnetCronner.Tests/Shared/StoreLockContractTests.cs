using Shouldly;

namespace DotnetCronner.Tests.Shared;

/// <summary>
/// The <see cref="ICronnerStore"/> lock contract, run against every shipped store (and, in the integration
/// project, against real PostgreSQL / SQL Server / Redis). These are the tests a consumer can point at when it
/// deletes its own "already running" guard: a due task is claimed exactly once even under contention, a claim is
/// renewed/released only by its owner, a stale full-row write can never clear someone else's lock, and orphaned
/// history rows are closed.
/// </summary>
public abstract class StoreLockContractTests : IAsyncLifetime
{
    protected IStoreBackend Backend { get; private set; } = default!;

    protected abstract Task<IStoreBackend> CreateBackendAsync();

    public async Task InitializeAsync() => Backend = await CreateBackendAsync();

    public Task DisposeAsync() => Backend.DisposeAsync().AsTask();

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    protected static CronnerJob NewJob(string id, DateTimeOffset due, CronnerTaskPriority priority = CronnerTaskPriority.Normal)
    {
        var now = DateTimeOffset.UtcNow;
        return new CronnerJob
        {
            Id = id,
            Name = id,
            CronExpression = "* * * * *",
            NextRunUtc = due,
            State = CronnerTaskState.Scheduled,
            Priority = priority,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private async Task<string[]> SeedDueJobsAsync(ICronnerStore store, int count, DateTimeOffset due)
    {
        var ids = new string[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = $"job-{i:000}";
            await store.UpsertAsync(NewJob(ids[i], due));
        }

        return ids;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The claim
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Contended_Claim_Hands_Out_Every_Due_Job_Exactly_Once()
    {
        const int jobs = 40, instances = 8;
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        var seeder = Backend.CreateStore();
        var ids = await SeedDueJobsAsync(seeder, jobs, due);

        // Every instance has its own store and owner; all of them fire AcquireDueAsync at the same instant.
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var claims = Enumerable.Range(0, instances).Select(async i =>
        {
            var store = Backend.CreateStore();
            await go.Task;
            return (Owner: $"owner-{i}", Jobs: await store.AcquireDueAsync(now, $"owner-{i}", Ttl, max: jobs));
        }).ToArray();
        go.SetResult();
        var results = await Task.WhenAll(claims);

        var all = results.SelectMany(r => r.Jobs.Select(j => (j.Id, r.Owner))).ToArray();
        all.Length.ShouldBe(jobs, $"{Backend.Name}: every due job must be claimed once — {all.Length} claims for {jobs} jobs");
        all.Select(c => c.Id).Distinct().Count().ShouldBe(jobs, $"{Backend.Name}: a job was handed to more than one instance");
        all.Select(c => c.Id).OrderBy(x => x).ShouldBe(ids.OrderBy(x => x));

        // The persisted view agrees with what was handed out: each job is locked by exactly the instance that got it.
        foreach (var (id, owner) in all)
        {
            var persisted = (await seeder.GetByIdAsync(id))!;
            persisted.State.ShouldBe(CronnerTaskState.Queued);
            persisted.LockOwner.ShouldBe(owner);
            persisted.LockedUntilUtc.ShouldNotBeNull();
        }

        // And nothing is left to claim.
        (await Backend.CreateStore().AcquireDueAsync(DateTimeOffset.UtcNow, "late", Ttl, max: jobs)).ShouldBeEmpty();
    }

    [ContractFact]
    public async Task Contended_Small_Batch_Claims_Drain_Every_Job_Without_Duplicates()
    {
        // Small batches + many instances: a store that only ever looks at the first `max` candidates (and finds
        // them locked by someone else) would starve an instance with free capacity; a store without an atomic
        // claim would hand a job out twice.
        const int jobs = 40, instances = 6, batch = 3;
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        await SeedDueJobsAsync(Backend.CreateStore(), jobs, due);

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var claims = Enumerable.Range(0, instances).Select(async i =>
        {
            var store = Backend.CreateStore();
            var mine = new List<string>();
            await go.Task;
            while (true)
            {
                var got = await store.AcquireDueAsync(now, $"owner-{i}", Ttl, max: batch);
                if (got.Count == 0)
                    break;
                got.Count.ShouldBeLessThanOrEqualTo(batch);
                mine.AddRange(got.Select(j => j.Id));
            }

            return mine;
        }).ToArray();
        go.SetResult();
        var results = await Task.WhenAll(claims);

        var all = results.SelectMany(r => r).ToArray();
        all.Length.ShouldBe(jobs, $"{Backend.Name}: every job must end up claimed by someone");
        all.Distinct().Count().ShouldBe(jobs, $"{Backend.Name}: a job was handed to more than one instance");
    }

    [ContractFact]
    public async Task Claim_Skips_A_Live_Foreign_Lock_And_Reclaims_An_Expired_One()
    {
        var a = Backend.CreateStore();
        var b = Backend.CreateStore();
        await a.UpsertAsync(NewJob("held", DateTimeOffset.UtcNow.AddSeconds(-1)));

        // A claims with a short lease.
        var claimed = await a.AcquireDueAsync(DateTimeOffset.UtcNow, "A", TimeSpan.FromMilliseconds(400), max: 10);
        claimed.ShouldHaveSingleItem().Id.ShouldBe("held");

        // While the lease is live B gets nothing …
        (await b.AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 10)).ShouldBeEmpty();

        // … once it lapses (A stopped renewing: a crash) B reclaims it.
        await Task.Delay(700);
        var reclaimed = await b.AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 10);
        reclaimed.ShouldHaveSingleItem().LockOwner.ShouldBe("B");

        // And A's claim is gone for good: it can neither renew nor release what B holds.
        (await a.RenewLockAsync("held", "A", DateTimeOffset.UtcNow + Ttl)).ShouldBeFalse();
        (await a.ReleaseLockAsync("held", "A")).ShouldBeFalse();
        (await b.GetByIdAsync("held"))!.LockOwner.ShouldBe("B");
    }

    [ContractFact]
    public async Task Claim_Orders_By_Priority_Then_Due_Time()
    {
        var store = Backend.CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(NewJob("low-early", now.AddMinutes(-2), CronnerTaskPriority.Low));
        await store.UpsertAsync(NewJob("high-late", now.AddMinutes(-1), CronnerTaskPriority.High));
        await store.UpsertAsync(NewJob("normal", now.AddMinutes(-3)));

        var first = await store.AcquireDueAsync(now, "A", Ttl, max: 1);
        first.ShouldHaveSingleItem().Id.ShouldBe("high-late");
        var second = await store.AcquireDueAsync(now, "A", Ttl, max: 1);
        second.ShouldHaveSingleItem().Id.ShouldBe("normal");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Renew
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Renew_Only_For_The_Owner()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await store.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        var later = DateTimeOffset.UtcNow.AddMinutes(10);
        (await Backend.CreateStore().RenewLockAsync("job", "B", later)).ShouldBeFalse();
        (await store.RenewLockAsync("missing", "A", later)).ShouldBeFalse();
        (await store.RenewLockAsync("job", "A", later)).ShouldBeTrue();

        var persisted = (await store.GetByIdAsync("job"))!;
        persisted.LockOwner.ShouldBe("A");
        persisted.LockedUntilUtc.ShouldNotBeNull();
        persisted.LockedUntilUtc!.Value.ShouldBe(later, tolerance: TimeSpan.FromSeconds(1));
    }

    [ContractFact]
    public async Task Renew_Refuses_A_Cancelled_Job()
    {
        // A cancel issued from another instance lands in the store as State = Cancelled while the owner is still
        // running; the owner's next renewal must be refused so the engine stops that run.
        var owner = Backend.CreateStore();
        var other = Backend.CreateStore();
        await owner.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await owner.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        var copy = (await other.GetByIdAsync("job"))!;
        copy.State = CronnerTaskState.Cancelled;
        copy.NextRunUtc = null;
        await other.UpsertAsync(copy);

        (await owner.RenewLockAsync("job", "A", DateTimeOffset.UtcNow + Ttl)).ShouldBeFalse();
        (await owner.GetByIdAsync("job"))!.State.ShouldBe(CronnerTaskState.Cancelled);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Release
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Release_Only_For_The_Owner_And_Frees_The_Claim()
    {
        var a = Backend.CreateStore();
        var b = Backend.CreateStore();
        await a.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await a.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        // A non-owner cannot release; the claim stays A's.
        (await b.ReleaseLockAsync("job", "B")).ShouldBeFalse();
        (await b.AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1)).ShouldBeEmpty();
        (await a.RenewLockAsync("job", "A", DateTimeOffset.UtcNow + Ttl)).ShouldBeTrue();

        // The owner releases; the job is free and the next claim wins it.
        (await a.ReleaseLockAsync("job", "A")).ShouldBeTrue();
        var persisted = (await a.GetByIdAsync("job"))!;
        persisted.LockOwner.ShouldBeNull();
        persisted.LockedUntilUtc.ShouldBeNull();
        (await b.AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1)).ShouldHaveSingleItem().LockOwner.ShouldBe("B");

        // A second release by the former owner is a no-op.
        (await a.ReleaseLockAsync("job", "A")).ShouldBeFalse();
        (await a.ReleaseLockAsync("missing", "A")).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Upsert never touches a lock
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Upsert_With_A_Stale_Snapshot_Never_Clears_A_Lock_Taken_In_The_Meantime()
    {
        // The scenario: a client read the job (unlocked), another instance claimed it, and the client now writes
        // its full snapshot back (TriggerNow, seeding at startup, …). The write must not free the claim.
        var a = Backend.CreateStore();
        var client = Backend.CreateStore();
        await a.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));

        var stale = (await client.GetByIdAsync("job"))!;   // LockOwner == null
        stale.LockOwner.ShouldBeNull();

        var claimed = (await a.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        stale.Name = "renamed by a stale writer";
        stale.Priority = CronnerTaskPriority.High;
        await client.UpsertAsync(stale);

        var persisted = (await client.GetByIdAsync("job"))!;
        persisted.Name.ShouldBe("renamed by a stale writer", "non-lock fields are written");
        persisted.LockOwner.ShouldBe("A", $"{Backend.Name}: Upsert cleared a lock it did not take");
        persisted.LockedUntilUtc.ShouldNotBeNull();
        persisted.LockedUntilUtc!.Value.ShouldBe(claimed.LockedUntilUtc!.Value, tolerance: TimeSpan.FromSeconds(1));

        // The claim is still effective, not just cosmetically present.
        (await Backend.CreateStore().AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1)).ShouldBeEmpty();
        (await a.RenewLockAsync("job", "A", DateTimeOffset.UtcNow + Ttl)).ShouldBeTrue();
    }

    [ContractFact]
    public async Task Upsert_With_A_Stale_Snapshot_Never_Shortens_A_Renewed_Lock()
    {
        var a = Backend.CreateStore();
        var client = Backend.CreateStore();
        await a.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await a.AcquireDueAsync(DateTimeOffset.UtcNow, "A", Ttl, max: 1)).ShouldHaveSingleItem();

        var stale = (await client.GetByIdAsync("job"))!;   // LockedUntilUtc == claim time + 1 min

        var renewedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        (await a.RenewLockAsync("job", "A", renewedUntil)).ShouldBeTrue();

        stale.Name = "stale";
        await client.UpsertAsync(stale);

        (await client.GetByIdAsync("job"))!.LockedUntilUtc!.Value.ShouldBe(renewedUntil, tolerance: TimeSpan.FromSeconds(1));
        // Five minutes from now the original expiry would have passed; the renewed one has not.
        (await Backend.CreateStore().AcquireDueAsync(DateTimeOffset.UtcNow.AddMinutes(5), "B", Ttl, max: 1)).ShouldBeEmpty();
    }

    [ContractFact]
    public async Task Upsert_Of_A_Cancelled_Job_Keeps_The_Lock_But_Makes_It_Unclaimable()
    {
        // CancelTaskAsync from another instance: State = Cancelled, NextRunUtc = null, lock untouched. The running
        // owner stops at its next renewal (refused); nobody can claim the task in between.
        var a = Backend.CreateStore();
        var client = Backend.CreateStore();
        await a.UpsertAsync(NewJob("job", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await a.AcquireDueAsync(DateTimeOffset.UtcNow, "A", TimeSpan.FromMilliseconds(300), max: 1)).ShouldHaveSingleItem();

        var copy = (await client.GetByIdAsync("job"))!;
        copy.State = CronnerTaskState.Cancelled;
        copy.NextRunUtc = null;
        await client.UpsertAsync(copy);

        (await client.GetByIdAsync("job"))!.LockOwner.ShouldBe("A");
        await Task.Delay(500); // lease lapsed
        (await Backend.CreateStore().AcquireDueAsync(DateTimeOffset.UtcNow, "B", Ttl, max: 1)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Orphaned history
    // ---------------------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task FinalizeOrphaned_Marks_Only_Running_Rows_Of_That_Job()
    {
        var store = Backend.CreateStore();
        await store.UpsertAsync(NewJob("a", DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.UpsertAsync(NewJob("b", DateTimeOffset.UtcNow.AddSeconds(-1)));

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var aRunning = new CronnerJobExecution { Id = "a-1", JobId = "a", StartedAt = t0, Status = JobExecutionStatus.Running, Attempt = 1, Owner = "dead" };
        var aDone = new CronnerJobExecution { Id = "a-2", JobId = "a", StartedAt = t0.AddMinutes(1), FinishedAt = t0.AddMinutes(2), Status = JobExecutionStatus.Succeeded, Attempt = 1, Owner = "x" };
        var bRunning = new CronnerJobExecution { Id = "b-1", JobId = "b", StartedAt = t0, Status = JobExecutionStatus.Running, Attempt = 1, Owner = "alive" };
        await store.RecordExecutionStartedAsync(aRunning);
        await store.RecordExecutionStartedAsync(aDone);
        await store.RecordExecutionFinishedAsync(aDone);
        await store.RecordExecutionStartedAsync(bRunning);

        var finishedAt = DateTimeOffset.UtcNow;
        (await store.FinalizeOrphanedExecutionsAsync("a", finishedAt, CronnerExecutionErrors.Orphaned)).ShouldBe(1);

        var a = await store.GetExecutionsAsync("a", 10);
        var orphan = a.Single(e => e.Id == "a-1");
        orphan.Status.ShouldBe(JobExecutionStatus.Failed);
        orphan.Error.ShouldBe(CronnerExecutionErrors.Orphaned);
        orphan.FinishedAt.ShouldNotBeNull();
        orphan.FinishedAt!.Value.ShouldBe(finishedAt, tolerance: TimeSpan.FromSeconds(1));
        a.Single(e => e.Id == "a-2").Status.ShouldBe(JobExecutionStatus.Succeeded);
        (await store.GetExecutionsAsync("b", 10)).Single().Status.ShouldBe(JobExecutionStatus.Running);

        // Idempotent.
        (await store.FinalizeOrphanedExecutionsAsync("a", finishedAt, CronnerExecutionErrors.Orphaned)).ShouldBe(0);
    }
}
