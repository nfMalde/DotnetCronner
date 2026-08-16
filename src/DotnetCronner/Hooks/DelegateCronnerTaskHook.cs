namespace DotnetCronner;

/// <summary>
/// An <see cref="ICronnerTaskHook"/> that runs a single delegate for one event, used by the fluent
/// <c>On*</c> helpers on the builder and the schedule options.
/// </summary>
internal sealed class DelegateCronnerTaskHook : ICronnerTaskHook
{
    private readonly CronnerHookEvent _hookEvent;
    private readonly Func<CronnerTaskContext, Task> _handler;

    public DelegateCronnerTaskHook(CronnerHookEvent hookEvent, Func<CronnerTaskContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _hookEvent = hookEvent;
        _handler = handler;
    }

    private Task RunIf(CronnerHookEvent hookEvent, CronnerTaskContext context) =>
        hookEvent == _hookEvent ? _handler(context) : Task.CompletedTask;

    public Task OnStartAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.Start, context);
    public Task OnSuccessAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.Success, context);
    public Task OnFailAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.Fail, context);
    public Task OnCancelAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.Cancel, context);
    public Task OnLockAcquireAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.LockAcquire, context);
    public Task OnKeepAliveAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.KeepAlive, context);
    public Task OnLockReleaseAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.LockRelease, context);
    public Task OnLockLostAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.LockLost, context);
    public Task OnTotalProgressChangeAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.TotalProgressChange, context);
    public Task OnProgressScopeOpenedAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.ProgressScopeOpened, context);
    public Task OnScopeProgressAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.ScopeProgress, context);
    public Task OnProgressScopeClosedAsync(CronnerTaskContext context) => RunIf(CronnerHookEvent.ProgressScopeClosed, context);
}
