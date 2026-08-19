namespace DotnetCronner.Tests.Shared;

/// <summary>
/// One storage backend (an in-memory store, a SQLite file, a PostgreSQL database, a Redis key prefix, …) the
/// shared contract tests run against. <see cref="CreateStore"/> returns a store instance connected to it — the
/// view one scheduler instance would have — so tests can emulate several instances sharing one backend.
/// </summary>
public interface IStoreBackend : IAsyncDisposable
{
    /// <summary>A human-readable name used in assertion messages.</summary>
    string Name { get; }

    /// <summary>
    /// Whether <see cref="CreateStore"/> hands out genuinely independent instances (own connections) — false for
    /// the in-memory store, which can only ever model one process.
    /// </summary>
    bool SupportsMultipleInstances { get; }

    /// <summary>Creates a store over this backend. Every call represents another scheduler instance.</summary>
    ICronnerStore CreateStore();
}
