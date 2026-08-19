using DotnetCronner.Tests.Shared;

namespace DotnetCronner.IntegrationTests;

// The shared lock contract and the two-scheduler exclusivity suite against real backends. These are the tests the
// README points at for the "one run per task across processes" guarantee: the in-memory/SQLite runs in the unit
// project prove the engine; these prove the atomic claim of each store on its actual database/server.

// ---- PostgreSQL ------------------------------------------------------------------------------------------------

[Collection(PostgresCollection.Name)]
public sealed class PostgresStoreLockContractTests(PostgresFixture fixture) : StoreLockContractTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await PostgresBackend.CreateAsync(fixture);
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresSchedulerExclusivityTests(PostgresFixture fixture) : SchedulerExclusivityTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await PostgresBackend.CreateAsync(fixture);
}

// ---- SQL Server (locking READ COMMITTED, the on-prem default) ---------------------------------------------------

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlStoreLockContractTests(MsSqlFixture fixture) : StoreLockContractTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await MsSqlBackend.CreateAsync(fixture, readCommittedSnapshot: false);
}

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlSchedulerExclusivityTests(MsSqlFixture fixture) : SchedulerExclusivityTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await MsSqlBackend.CreateAsync(fixture, readCommittedSnapshot: false);
}

// ---- SQL Server with READ_COMMITTED_SNAPSHOT (the Azure SQL default) -------------------------------------------

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlSnapshotStoreLockContractTests(MsSqlFixture fixture) : StoreLockContractTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await MsSqlBackend.CreateAsync(fixture, readCommittedSnapshot: true);
}

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlSnapshotSchedulerExclusivityTests(MsSqlFixture fixture) : SchedulerExclusivityTests
{
    protected override async Task<IStoreBackend> CreateBackendAsync() => await MsSqlBackend.CreateAsync(fixture, readCommittedSnapshot: true);
}

// ---- Redis ------------------------------------------------------------------------------------------------------

[Collection(RedisCollection.Name)]
public sealed class RedisStoreLockContractTests(RedisFixture fixture) : StoreLockContractTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new RedisBackend(fixture));
}

[Collection(RedisCollection.Name)]
public sealed class RedisSchedulerExclusivityTests(RedisFixture fixture) : SchedulerExclusivityTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new RedisBackend(fixture));
}
