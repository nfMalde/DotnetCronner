using DotnetCronner.Tests.Shared;

namespace DotnetCronner.Tests.Backends;

/// <summary>One <see cref="InMemoryCronnerStore"/>; every "instance" is the same object (it models one process).</summary>
public sealed class InMemoryBackend : IStoreBackend
{
    private readonly InMemoryCronnerStore _store = new();

    public string Name => "InMemory";

    public bool SupportsMultipleInstances => false;

    public ICronnerStore CreateStore() => _store;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
