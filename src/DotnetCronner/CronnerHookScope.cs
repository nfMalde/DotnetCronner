namespace DotnetCronner;

/// <summary>
/// Which DI scope a <em>terminal</em> lifecycle hook (<c>OnStart</c> / <c>OnSuccess</c> / <c>OnFail</c> /
/// <c>OnCancel</c>) runs in. Set it per hook via <c>AddHook</c> / <c>WithHook</c>, or leave a hook to inherit
/// the default in <see cref="CronnerOptions.HookScope"/>. Lock and progress hooks always run in their own
/// fresh scope and ignore this.
/// </summary>
public enum CronnerHookScope
{
    /// <summary>
    /// Terminal hooks run in the job's own execution scope, so a hook's <c>ctx.HasParam&lt;T&gt;()</c>
    /// resolves the same scoped instances the job used (e.g. read back a summary the job wrote). The default.
    /// </summary>
    Shared = 0,

    /// <summary>
    /// Every hook invocation — terminal ones included — runs in its own fresh DI scope, fully isolated from
    /// the job's scope. Use this when the job's scope holds a single-session unit of work that a hook must
    /// not contend with. Pass data from the job to the hook via the run-state bag (<c>ctx.Set</c> /
    /// <c>ctx.Get</c>) instead of shared scoped services.
    /// </summary>
    Isolated = 1,
}
