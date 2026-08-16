namespace DotnetCronner;

/// <summary>
/// Holds hooks registered fluently on the builder (instances and delegates). Registered as a singleton
/// so it works whether configured at <c>AddDotnetCronner</c> or <c>app.UseDotnetCronner</c> time. Hooks
/// registered through DI as <see cref="ICronnerTaskHook"/> are picked up separately, per execution scope.
/// </summary>
public sealed class CronnerHookRegistry
{
    private readonly object _gate = new();
    private readonly List<ICronnerTaskHook> _hooks = [];

    /// <summary>Adds a hook instance.</summary>
    public void Add(ICronnerTaskHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_gate)
            _hooks.Add(hook);
    }

    /// <summary>A snapshot of the registered hooks.</summary>
    public IReadOnlyList<ICronnerTaskHook> Hooks
    {
        get
        {
            lock (_gate)
                return _hooks.ToArray();
        }
    }
}
