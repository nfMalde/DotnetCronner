using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DotnetCronner;

/// <summary>
/// The process-wide catalog of registered task descriptors, keyed by id. Registered as a singleton.
/// </summary>
public sealed class CronnerRegistry
{
    private readonly ConcurrentDictionary<string, CronnerJobDescriptor> _byId = new(StringComparer.Ordinal);

    /// <summary>Adds a descriptor. Throws <see cref="DuplicateCronnerTaskException"/> if the id is already registered.</summary>
    public void Add(CronnerJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_byId.TryAdd(descriptor.Id, descriptor))
            throw new DuplicateCronnerTaskException(descriptor.Id);
    }

    /// <summary>Looks up a descriptor by id.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out CronnerJobDescriptor? descriptor) =>
        _byId.TryGetValue(id, out descriptor);

    /// <summary>All registered descriptors.</summary>
    public IReadOnlyCollection<CronnerJobDescriptor> Descriptors => (IReadOnlyCollection<CronnerJobDescriptor>)_byId.Values;
}
