using DotnetCronner.Tests.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotnetCronner.Tests.Backends;

/// <summary>
/// A SQLite database FILE (WAL mode) shared by any number of <see cref="EfCronnerStore{TContext}"/> instances, each
/// with its own connections — unlike the shared in-memory connection of the basic EF tests, this lets several
/// "instances" hit the same database concurrently and exercises the conditional-UPDATE claim for real.
/// </summary>
public sealed class SqliteFileBackend : IStoreBackend
{
    public sealed class SqliteCronnerDbContext(DbContextOptions<SqliteCronnerDbContext> options) : DbContext(options), ICronnerDbContext
    {
        public DbSet<CronnerJobEntity> CronnerJobs => Set<CronnerJobEntity>();

        public DbSet<CronnerJobExecutionEntity> CronnerJobExecutions => Set<CronnerJobExecutionEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCronnerModel();

        // SQLite stores DateTimeOffset as TEXT and cannot translate comparisons on it; a sortable binary works.
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    private sealed class Factory(DbContextOptions<SqliteCronnerDbContext> options) : IDbContextFactory<SqliteCronnerDbContext>
    {
        public SqliteCronnerDbContext CreateDbContext() => new(options);
    }

    private readonly string _path;
    private readonly DbContextOptions<SqliteCronnerDbContext> _options;

    public SqliteFileBackend()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cronner-tests-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _path, ForeignKeys = true, Pooling = false }.ToString();
        _options = new DbContextOptionsBuilder<SqliteCronnerDbContext>().UseSqlite(connectionString).Options;

        using var context = new SqliteCronnerDbContext(_options);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public string Name => "EF Core / SQLite file";

    public bool SupportsMultipleInstances => true;

    public ICronnerStore CreateStore() => new EfCronnerStore<SqliteCronnerDbContext>(new Factory(_options));

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }

        return ValueTask.CompletedTask;
    }
}
