namespace DotnetCronner;

/// <summary>
/// A lifecycle hook invoked around task execution — use it for logging, metrics, notifications, or
/// custom error handling. Register any number of hooks; each one only needs to override the methods it
/// cares about. A hook that throws is logged and ignored so it never breaks task execution.
/// </summary>
/// <remarks>
/// Register hooks either through DI (resolved per invocation, so they may use scoped services):
/// <code>services.AddScoped&lt;ICronnerTaskHook, MyHook&gt;();</code>
/// or fluently on the builder (global) or per schedule via <c>AddHook</c> / <c>OnSuccess</c> /
/// <c>OnLockLost</c> / etc. Every hook method is invoked in its own fresh DI scope; resolve scoped
/// services through <see cref="CronnerTaskContext.Services"/> or <see cref="CronnerTaskContext.HasParam{T}()"/>.
/// </remarks>
public interface ICronnerTaskHook
{
    /// <summary>Runs immediately before the task method is invoked.</summary>
    Task OnStartAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs after the task completes successfully.</summary>
    Task OnSuccessAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs after the task throws. The exception is available on <see cref="CronnerTaskContext.Exception"/>.</summary>
    Task OnFailAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs after the task is cancelled via <see cref="ICronnerClient.CancelTaskAsync"/>.</summary>
    Task OnCancelAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when the execution lock for the task is claimed, before it starts running.</summary>
    Task OnLockAcquireAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs each time the execution lock is renewed (the keepalive) while the task runs.</summary>
    Task OnKeepAliveAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when the execution lock is released after the task finishes (or is released up front in concurrent mode).</summary>
    Task OnLockReleaseAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>
    /// Runs when the execution lock is lost mid-run — a keepalive renewal failed because the claim was
    /// reclaimed or released elsewhere — and the run was cancelled to avoid a duplicate execution.
    /// </summary>
    Task OnLockLostAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when total progress is reported (see <see cref="CronnerTaskContext.TotalProgress"/>).</summary>
    Task OnTotalProgressChangeAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when a progress scope is opened (see <see cref="CronnerTaskContext.ProgressScope"/>).</summary>
    Task OnProgressScopeOpenedAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when progress is reported to a scope (see <see cref="CronnerTaskContext.ProgressScope"/>).</summary>
    Task OnScopeProgressAsync(CronnerTaskContext context) => Task.CompletedTask;

    /// <summary>Runs when a progress scope is closed (see <see cref="CronnerTaskContext.ProgressScope"/>).</summary>
    Task OnProgressScopeClosedAsync(CronnerTaskContext context) => Task.CompletedTask;
}
