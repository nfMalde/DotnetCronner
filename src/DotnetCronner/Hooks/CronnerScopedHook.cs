namespace DotnetCronner;

/// <summary>
/// Internal marker for a hook that was registered with an explicit <see cref="CronnerHookScope"/> (via
/// <c>AddHook</c> / <c>WithHook</c>). The dispatcher reads <see cref="PreferredScope"/> for the terminal
/// lifecycle events; a hook that does not implement this — or whose preference is <c>null</c> — inherits
/// <see cref="CronnerOptions.HookScope"/>. It never affects lock/progress hooks, which always run isolated.
/// </summary>
internal interface ICronnerScopedHook
{
    /// <summary>The hook's own scope preference, or <c>null</c> to inherit the run's default.</summary>
    CronnerHookScope? PreferredScope { get; }
}

/// <summary>
/// Carries a scope preference for a caller-supplied hook instance without the caller's type needing to know
/// about it. Never invoked directly: the dispatcher reads the scope and dispatches to <see cref="Inner"/>.
/// </summary>
internal sealed class ScopedHookWrapper(ICronnerTaskHook inner, CronnerHookScope scope) : ICronnerTaskHook, ICronnerScopedHook
{
    /// <summary>The wrapped hook the dispatcher actually invokes.</summary>
    public ICronnerTaskHook Inner { get; } = inner;

    /// <inheritdoc />
    public CronnerHookScope? PreferredScope { get; } = scope;
}
