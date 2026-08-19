using DotnetCronner.Tests.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace DotnetCronner.IntegrationTests;

// One container per backend for the whole test run (collection fixtures); every test gets its own database (or
// key prefix) on it so tests never see each other's jobs — a scheduler claims ANY due row in its store.

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

[CollectionDefinition(Name)]
public sealed class MsSqlCollection : ICollectionFixture<MsSqlFixture>
{
    public const string Name = "mssql";
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => DockerProbe.IsAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => DockerProbe.IsAvailable ? _container.DisposeAsync().AsTask() : Task.CompletedTask;
}

public sealed class MsSqlFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => DockerProbe.IsAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => DockerProbe.IsAvailable ? _container.DisposeAsync().AsTask() : Task.CompletedTask;
}

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();
    private IConnectionMultiplexer? _multiplexer;

    public string ConnectionString => _container.GetConnectionString();

    public IConnectionMultiplexer Multiplexer => _multiplexer ?? throw new InvalidOperationException("Redis fixture not started.");

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable)
            return;
        await _container.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_multiplexer is not null)
            await _multiplexer.DisposeAsync();
        if (DockerProbe.IsAvailable)
            await _container.DisposeAsync();
    }
}

// ------------------------------------------------------------------------------------------------------------
// Backends: one fresh database / key prefix per test.
// ------------------------------------------------------------------------------------------------------------

public sealed class PostgresBackend : IStoreBackend
{
    private sealed class Factory(DbContextOptions<CronnerDbContext> options) : IDbContextFactory<CronnerDbContext>
    {
        public CronnerDbContext CreateDbContext() => new(options);
    }

    private readonly string _admin;
    private readonly string _database;
    private readonly DbContextOptions<CronnerDbContext> _options;

    private PostgresBackend(string admin, string database, DbContextOptions<CronnerDbContext> options)
    {
        _admin = admin;
        _database = database;
        _options = options;
    }

    public static async Task<PostgresBackend> CreateAsync(PostgresFixture fixture)
    {
        var database = $"cronner_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database }.ToString();
        var options = new DbContextOptionsBuilder<CronnerDbContext>().UseNpgsql(cs).Options;
        await using (var context = new CronnerDbContext(options))
            await context.Database.EnsureCreatedAsync();

        return new PostgresBackend(fixture.AdminConnectionString, database, options);
    }

    public string Name => "EF Core / PostgreSQL";

    public bool SupportsMultipleInstances => true;

    public string ConnectionString => new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ToString();

    public ICronnerStore CreateStore() => new EfCronnerStore<CronnerDbContext>(new Factory(_options));

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class MsSqlBackend : IStoreBackend
{
    private sealed class Factory(DbContextOptions<CronnerDbContext> options) : IDbContextFactory<CronnerDbContext>
    {
        public CronnerDbContext CreateDbContext() => new(options);
    }

    private readonly string _admin;
    private readonly string _database;
    private readonly bool _rcsi;
    private readonly DbContextOptions<CronnerDbContext> _options;

    private MsSqlBackend(string admin, string database, bool rcsi, DbContextOptions<CronnerDbContext> options)
    {
        _admin = admin;
        _database = database;
        _rcsi = rcsi;
        _options = options;
    }

    /// <param name="readCommittedSnapshot">
    /// Whether to turn READ_COMMITTED_SNAPSHOT on (the Azure SQL default) — the claim must be race-safe under both
    /// locking and versioning READ COMMITTED.
    /// </param>
    public static async Task<MsSqlBackend> CreateAsync(MsSqlFixture fixture, bool readCommittedSnapshot)
    {
        var database = $"cronner_{Guid.NewGuid():N}";
        await using (var admin = new SqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{database}]", admin);
            await create.ExecuteNonQueryAsync();
            if (readCommittedSnapshot)
            {
                await using var rcsi = new SqlCommand($"ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", admin);
                await rcsi.ExecuteNonQueryAsync();
            }
        }

        var cs = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = database }.ToString();
        var options = new DbContextOptionsBuilder<CronnerDbContext>().UseSqlServer(cs).Options;
        await using (var context = new CronnerDbContext(options))
            await context.Database.EnsureCreatedAsync();

        return new MsSqlBackend(fixture.AdminConnectionString, database, readCommittedSnapshot, options);
    }

    public string Name => _rcsi ? "EF Core / SQL Server (READ_COMMITTED_SNAPSHOT ON)" : "EF Core / SQL Server";

    public bool SupportsMultipleInstances => true;

    public string ConnectionString => new SqlConnectionStringBuilder(_admin) { InitialCatalog = _database }.ToString();

    public ICronnerStore CreateStore() => new EfCronnerStore<CronnerDbContext>(new Factory(_options));

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var admin = new SqlConnection(_admin);
        await admin.OpenAsync();
        await using var cmd = new SqlCommand($"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class RedisBackend(RedisFixture fixture) : IStoreBackend
{
    private readonly string _prefix = $"it:{Guid.NewGuid():N}:";

    public string Name => "Redis";

    public bool SupportsMultipleInstances => true;

    public string KeyPrefix => _prefix;

    // Every store instance gets its own multiplexer, like a separate process would.
    public ICronnerStore CreateStore() => new RedisCronnerStore(ConnectionMultiplexer.Connect(fixture.ConnectionString), _prefix);

    public async ValueTask DisposeAsync()
    {
        var db = fixture.Multiplexer.GetDatabase();
        foreach (var endpoint in fixture.Multiplexer.GetEndPoints())
        {
            var server = fixture.Multiplexer.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{_prefix}*"))
                await db.KeyDeleteAsync(key);
        }
    }
}
