using DotnetCronner;
using Shouldly;

namespace DotnetCronner.Tests;

public class InMemoryCronnerStoreTests
{
    private static CronnerJob NewJob(string id, DateTimeOffset? next, CronnerTaskPriority priority = CronnerTaskPriority.Normal) => new()
    {
        Id = id,
        Name = id,
        CronExpression = "* * * * *",
        Priority = priority,
        NextRunUtc = next,
        State = next is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Idle,
    };

    [Fact]
    public async Task Upsert_And_GetById_RoundTrips()
    {
        var store = new InMemoryCronnerStore();
        await store.UpsertAsync(NewJob("a", DateTimeOffset.UtcNow));

        var fetched = await store.GetByIdAsync("a");
        fetched.ShouldNotBeNull();
        fetched!.Id.ShouldBe("a");
    }

    [Fact]
    public async Task GetById_ReturnsSnapshot_NotSharedReference()
    {
        var store = new InMemoryCronnerStore();
        await store.UpsertAsync(NewJob("a", DateTimeOffset.UtcNow));

        var first = await store.GetByIdAsync("a");
        first!.State = CronnerTaskState.Failed;
        var second = await store.GetByIdAsync("a");

        second!.State.ShouldNotBe(CronnerTaskState.Failed);
    }

    [Fact]
    public async Task Get_FiltersByState_AndPages()
    {
        var store = new InMemoryCronnerStore();
        for (var i = 0; i < 5; i++)
            await store.UpsertAsync(NewJob($"j{i}", DateTimeOffset.UtcNow));

        (await store.GetAsync(CronnerTaskState.Scheduled, 0, 10)).Count.ShouldBe(5);
        (await store.GetAsync(null, 1, 2)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task AcquireDue_ClaimsDueJobs_AndLocksThem()
    {
        var store = new InMemoryCronnerStore();
        await store.UpsertAsync(NewJob("due", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.UpsertAsync(NewJob("future", DateTimeOffset.UtcNow.AddMinutes(10)));

        var claimed = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner-1", TimeSpan.FromMinutes(1), 10);

        claimed.ShouldHaveSingleItem();
        claimed[0].Id.ShouldBe("due");
        claimed[0].State.ShouldBe(CronnerTaskState.Queued);
        claimed[0].LockOwner.ShouldBe("owner-1");
    }

    [Fact]
    public async Task AcquireDue_DoesNotReclaimLockedJob()
    {
        var store = new InMemoryCronnerStore();
        await store.UpsertAsync(NewJob("due", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var first = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner-1", TimeSpan.FromMinutes(5), 10);
        var second = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner-2", TimeSpan.FromMinutes(5), 10);

        first.ShouldHaveSingleItem();
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewLock_ExtendsClaim_SoItIsNotReclaimed()
    {
        var store = new InMemoryCronnerStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(NewJob("due", now.AddMinutes(-1)));

        var claimed = await store.AcquireDueAsync(now, "owner-1", TimeSpan.FromSeconds(30), 10);
        claimed.ShouldHaveSingleItem();

        // Push the lock well past the point at which it would otherwise have expired.
        (await store.RenewLockAsync("due", "owner-1", now.AddMinutes(10))).ShouldBeTrue();

        // A minute later the original 30s claim would be reclaimable — the renewal keeps it locked.
        var reclaim = await store.AcquireDueAsync(now.AddMinutes(1), "owner-2", TimeSpan.FromSeconds(30), 10);
        reclaim.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewLock_FailsForNonOwner_AndUnknownJob()
    {
        var store = new InMemoryCronnerStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(NewJob("due", now.AddMinutes(-1)));
        await store.AcquireDueAsync(now, "owner-1", TimeSpan.FromMinutes(1), 10);

        (await store.RenewLockAsync("due", "someone-else", now.AddMinutes(10))).ShouldBeFalse();
        (await store.RenewLockAsync("missing", "owner-1", now.AddMinutes(10))).ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireDue_OrdersByPriority()
    {
        var store = new InMemoryCronnerStore();
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.UpsertAsync(NewJob("low", due, CronnerTaskPriority.Low));
        await store.UpsertAsync(NewJob("critical", due, CronnerTaskPriority.Critical));
        await store.UpsertAsync(NewJob("normal", due, CronnerTaskPriority.Normal));

        var claimed = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner", TimeSpan.FromMinutes(1), 1);

        claimed.ShouldHaveSingleItem();
        claimed[0].Id.ShouldBe("critical");
    }
}
