using DotnetCronner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Shouldly;

namespace DotnetCronner.Tests;

public class EfCronnerStoreTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), ICronnerDbContext
    {
        public DbSet<CronnerJobEntity> CronnerJobs => Set<CronnerJobEntity>();

        public DbSet<CronnerJobExecutionEntity> CronnerJobExecutions => Set<CronnerJobExecutionEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCronnerModel();

        // SQLite stores DateTimeOffset as TEXT and can't translate comparisons on it; store it as a
        // sortable binary for the tests (SQL Server / PostgreSQL compare DateTimeOffset natively).
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    // SQLite in-memory (a real relational provider, unlike EF's InMemory) so ExecuteUpdate is exercised.
    // The connection is kept open for the fixture's lifetime — closing it drops the database.
    private sealed class SqliteContextFactory : IDbContextFactory<TestDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<TestDbContext> _options;

        public SqliteContextFactory()
        {
            // Foreign Keys=True so SQLite actually enforces the execution→job FK (and its ON DELETE CASCADE);
            // it is off by default on a shared, externally-opened connection.
            _connection = new SqliteConnection("Filename=:memory:;Foreign Keys=True");
            _connection.Open();
            _options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;
            using var context = new TestDbContext(_options);
            context.Database.EnsureCreated();
        }

        public TestDbContext CreateDbContext() => new(_options);

        public void Dispose() => _connection.Dispose();
    }

    private static CronnerJob Job(string id, DateTimeOffset? next) => new()
    {
        Id = id,
        Name = id,
        CronExpression = "* * * * *",
        NextRunUtc = next,
        State = next is not null ? CronnerTaskState.Scheduled : CronnerTaskState.Idle,
    };

    [Fact]
    public async Task Upsert_Insert_Then_Update()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("a", DateTimeOffset.UtcNow));

        var loaded = await store.GetByIdAsync("a");
        loaded.ShouldNotBeNull();

        loaded!.State = CronnerTaskState.Completed;
        await store.UpsertAsync(loaded);

        (await store.GetByIdAsync("a"))!.State.ShouldBe(CronnerTaskState.Completed);
    }

    [Fact]
    public async Task AcquireDue_ClaimsAndLocks()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("due", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.UpsertAsync(Job("future", DateTimeOffset.UtcNow.AddHours(1)));

        var claimed = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner", TimeSpan.FromMinutes(1), 10);

        claimed.ShouldHaveSingleItem();
        claimed[0].Id.ShouldBe("due");
        claimed[0].State.ShouldBe(CronnerTaskState.Queued);
        claimed[0].LockOwner.ShouldBe("owner");
    }

    [Fact]
    public async Task AcquireDue_DoesNotHandOutTheSameJobTwice()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("due", DateTimeOffset.UtcNow.AddMinutes(-1)));

        // Two competing owners against the one database — the conditional UPDATE lets only one win.
        var first = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner-1", TimeSpan.FromMinutes(5), 10);
        var second = await store.AcquireDueAsync(DateTimeOffset.UtcNow, "owner-2", TimeSpan.FromMinutes(5), 10);

        first.ShouldHaveSingleItem();
        first[0].LockOwner.ShouldBe("owner-1");
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewLock_OnlyWhileOwned_ExtendsTheClaim()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(Job("due", now.AddMinutes(-1)));

        await store.AcquireDueAsync(now, "owner-1", TimeSpan.FromSeconds(30), 10);

        // The owner can extend; a different worker cannot; a missing job cannot.
        (await store.RenewLockAsync("due", "owner-1", now.AddMinutes(10))).ShouldBeTrue();
        (await store.RenewLockAsync("due", "owner-2", now.AddMinutes(10))).ShouldBeFalse();
        (await store.RenewLockAsync("missing", "owner-1", now.AddMinutes(10))).ShouldBeFalse();

        // With the lock extended, a later poll must not reclaim the still-running job.
        var reclaim = await store.AcquireDueAsync(now.AddMinutes(1), "owner-2", TimeSpan.FromSeconds(30), 10);
        reclaim.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesJob()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("a", DateTimeOffset.UtcNow));
        await store.RemoveAsync("a");
        (await store.GetByIdAsync("a")).ShouldBeNull();
    }

    // ---- execution history --------------------------------------------------------------------

    [Fact]
    public async Task ExecutionHistory_Started_Then_Finished_Finalizes_The_Same_Row()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("j", DateTimeOffset.UtcNow));

        var run = new CronnerJobExecution
        {
            Id = "corr-1", JobId = "j", StartedAt = DateTimeOffset.UtcNow, Attempt = 1,
        };
        await store.RecordExecutionStartedAsync(run);

        // The in-flight row reads back as Running with no finish time.
        var running = (await store.GetExecutionsAsync("j", 10)).ShouldHaveSingleItem();
        running.Status.ShouldBe(JobExecutionStatus.Running);
        running.FinishedAt.ShouldBeNull();

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Status = JobExecutionStatus.Failed;
        run.Error = "boom";
        await store.RecordExecutionFinishedAsync(run);

        // Finalized in place — still one row, now terminal (not a second inserted row).
        var finished = (await store.GetExecutionsAsync("j", 10)).ShouldHaveSingleItem();
        finished.Status.ShouldBe(JobExecutionStatus.Failed);
        finished.Error.ShouldBe("boom");
        finished.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecutionHistory_Prune_Keeps_Newest_And_Reads_Newest_First()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("j", DateTimeOffset.UtcNow));

        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.RecordExecutionStartedAsync(new CronnerJobExecution
            {
                Id = $"corr-{i}", JobId = "j", StartedAt = baseTime.AddSeconds(i), Attempt = 1,
            });

        await store.PruneExecutionsAsync("j", 2);

        var runs = await store.GetExecutionsAsync("j", 100);
        runs.Count.ShouldBe(2);
        // Newest first, and the older three were pruned.
        runs.Select(r => r.Id).ShouldBe(["corr-4", "corr-3"]);
    }

    [Fact]
    public async Task ExecutionHistory_Removed_Job_Cascades_Its_History()
    {
        using var factory = new SqliteContextFactory();
        var store = new EfCronnerStore<TestDbContext>(factory);
        await store.UpsertAsync(Job("j", DateTimeOffset.UtcNow));
        await store.RecordExecutionStartedAsync(new CronnerJobExecution
        {
            Id = "corr-1", JobId = "j", StartedAt = DateTimeOffset.UtcNow, Attempt = 1,
        });

        await store.RemoveAsync("j");

        (await store.GetExecutionsAsync("j", 10)).ShouldBeEmpty();
    }
}
