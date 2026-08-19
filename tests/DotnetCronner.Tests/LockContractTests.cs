using DotnetCronner.Tests.Backends;
using DotnetCronner.Tests.Shared;

namespace DotnetCronner.Tests;

// The shared store lock contract (tests/DotnetCronner.Tests/Shared/StoreLockContractTests.cs) against the stores
// that need no external service. The same contract runs against PostgreSQL, SQL Server and Redis in
// tests/DotnetCronner.IntegrationTests — that is where the cross-process evidence comes from; the in-memory store
// can only show in-process atomicity.

public sealed class InMemoryStoreLockContractTests : StoreLockContractTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new InMemoryBackend());
}

public sealed class SqliteFileStoreLockContractTests : StoreLockContractTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new SqliteFileBackend());
}

public sealed class InMemorySchedulerExclusivityTests : SchedulerExclusivityTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new InMemoryBackend());
}

public sealed class SqliteFileSchedulerExclusivityTests : SchedulerExclusivityTests
{
    protected override Task<IStoreBackend> CreateBackendAsync() => Task.FromResult<IStoreBackend>(new SqliteFileBackend());
}
