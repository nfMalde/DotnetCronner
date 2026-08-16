using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Hooks;

/// <summary>
/// The target of the <em>expression</em> hook style: <c>OnStart&lt;AuditHook&gt;(h => h.Record(...))</c>.
/// It is a plain class — no <c>ICronnerTaskHook</c>, no base type — whose arguments are resolved from the
/// hook's own DI scope through <c>HasParam&lt;T&gt;()</c>, exactly like a <c>Sched</c> lambda.
/// </summary>
/// <remarks>
/// Note what this style can and cannot see: arguments come from DI only, so an expression hook has no
/// access to the <c>CronnerTaskContext</c> (and therefore not to the job id) — use a delegate or an
/// <c>ICronnerTaskHook</c> when the hook needs to know which task fired.
/// </remarks>
public sealed class AuditHook(ILogger<AuditHook> logger)
{
    /// <summary>A synchronous expression hook: <c>Expression&lt;Action&lt;AuditHook&gt;&gt;</c>.</summary>
    public void Record(JobActivityLog activity, string phase)
    {
        logger.LogDebug("[expression hook] {Phase}", phase);
        activity.Record("hooks:expression", $"[expression hook] {phase} — target + HasParam<JobActivityLog>() resolved from the hook scope");
    }

    /// <summary>An asynchronous expression hook: <c>Expression&lt;Func&lt;AuditHook, Task&gt;&gt;</c>.</summary>
    public Task RecordAsync(JobActivityLog activity, ScopeMarker scope, string phase)
    {
        activity.Record("hooks:expression", $"[expression hook] {phase} (async, hook scope {scope.Id})");
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="ICronnerTaskHook"/> attached to a <em>single schedule</em> with <c>WithHook&lt;T&gt;()</c>
/// — it must never fire for any other task.
/// </summary>
public sealed class ScheduleScopedHook(JobActivityLog activity) : ICronnerTaskHook
{
    /// <inheritdoc />
    public Task OnStartAsync(CronnerTaskContext context)
    {
        activity.Record(context.Job.Id, "[per-schedule hook] WithHook<ScheduleScopedHook>() — attached to this one task only");
        return Task.CompletedTask;
    }
}
