using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// An <see cref="ICronnerTaskHook"/> registered as a method-call expression (the same style as
/// <c>Sched</c>). For its one event it resolves the target and its <c>HasParam</c> arguments from the
/// hook's own scope and invokes the method; every other event is a no-op.
/// </summary>
internal sealed class ExpressionCronnerTaskHook : ICronnerTaskHook
{
    private readonly CronnerHookEvent _hookEvent;
    private readonly Type _targetType;
    private readonly MethodInfo _method;
    private readonly IReadOnlyList<CronnerArgument> _arguments;

    public ExpressionCronnerTaskHook(
        CronnerHookEvent hookEvent, Type targetType, MethodInfo method, IReadOnlyList<CronnerArgument> arguments)
    {
        _hookEvent = hookEvent;
        _targetType = targetType;
        _method = method;
        _arguments = arguments;
    }

    private Task RunIf(CronnerHookEvent hookEvent, CronnerTaskContext context) =>
        hookEvent == _hookEvent
            ? CronnerJobInvoker.InvokeAsync(context.Services, _targetType, _method, _arguments, context.CancellationToken)
            : Task.CompletedTask;

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
