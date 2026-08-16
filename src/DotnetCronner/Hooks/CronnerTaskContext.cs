using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// The context passed to a lifecycle or lock hook. Each hook method is invoked in its own fresh DI scope;
/// <see cref="Services"/> is that scope's provider. <see cref="Duration"/> and <see cref="Exception"/> are
/// populated for the terminal (succeeded / failed / cancelled) events and are otherwise their defaults.
/// </summary>
public sealed class CronnerTaskContext
{
    /// <summary>A snapshot of the task the hook is firing for.</summary>
    public required CronnerJob Job { get; init; }

    /// <summary>The hook's own scoped service provider — resolve scoped services here.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>A cancellation token tied to the scheduler's lifetime; cancelled when the host is shutting down.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>How long the execution took. Zero outside the terminal events.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>The exception thrown by a failed execution, if any.</summary>
    public Exception? Exception { get; internal set; }

    /// <summary>The total progress reported, for the <c>OnTotalProgressChange</c> event (and current total on scope events).</summary>
    public decimal TotalProgress { get; internal set; }

    /// <summary>A snapshot of the progress scope, for the scope events (opened / progress / closed); otherwise <c>null</c>.</summary>
    public CronnerProgressInfo? ProgressScope { get; internal set; }

    /// <summary>Resolves a required service from this hook's scope — the same idea as <c>HasParam</c> in a <c>Sched</c> lambda.</summary>
    public T HasParam<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Resolves a value from this hook's scope using a caller-supplied factory.</summary>
    public T HasParam<T>(Func<IServiceProvider, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory(Services);
    }
}
