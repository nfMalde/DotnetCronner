using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetCronner;

/// <summary>
/// Decides which service provider task executions resolve from. By default this is the application's
/// own provider (a fresh scope is created per run). When <see cref="UseDedicated"/> is configured, a
/// separate, isolated provider is built from a caller-supplied service collection, and the discovered
/// job classes are registered into it with the chosen lifetime.
/// </summary>
public sealed class CronnerJobServices : IDisposable
{
    private readonly object _gate = new();
    private Action<IServiceCollection>? _configure;
    private ServiceLifetime _jobLifetime = ServiceLifetime.Scoped;
    private ServiceProvider? _dedicated;
    private bool _built;

    // Note: building a provider requires the DI implementation package (Microsoft.Extensions.DependencyInjection).

    /// <summary>Whether a dedicated, isolated provider is configured.</summary>
    public bool IsDedicated => _configure is not null;

    /// <summary>The lifetime the job classes are registered with in dedicated mode.</summary>
    public ServiceLifetime JobLifetime => _jobLifetime;

    /// <summary>Configures a dedicated service collection and the lifetime for job classes.</summary>
    public void UseDedicated(Action<IServiceCollection> configure, ServiceLifetime jobLifetime)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure = configure;
        _jobLifetime = jobLifetime;
    }

    /// <summary>
    /// Returns the provider task executions should resolve from: the application provider in default
    /// mode, or a lazily built dedicated provider (with <paramref name="jobTypes"/> registered) otherwise.
    /// </summary>
    public IServiceProvider Resolve(IServiceProvider applicationServices, IEnumerable<Type> jobTypes)
    {
        if (_configure is null)
            return applicationServices;

        if (_built)
            return _dedicated!;

        lock (_gate)
        {
            if (_built)
                return _dedicated!;

            IServiceCollection collection = new ServiceCollection();
            _configure(collection);
            foreach (var type in jobTypes.Distinct())
                collection.Add(new ServiceDescriptor(type, type, _jobLifetime));

            // The progress context must be resolvable in the dedicated provider too, so jobs there can report.
            collection.TryAddScoped<CronnerJobContext>();
            collection.TryAddScoped<ICronnerJobContext>(sp => sp.GetRequiredService<CronnerJobContext>());

            _dedicated = collection.BuildServiceProvider();
            _built = true;
            return _dedicated;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _dedicated?.Dispose();
}
