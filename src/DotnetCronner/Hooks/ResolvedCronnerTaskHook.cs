using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// An <see cref="ICronnerTaskHook"/> that resolves the real hook type from the hook's own scope on each
/// invocation (via DI, falling back to construction). Backs <c>AddHook&lt;T&gt;()</c> / <c>WithHook&lt;T&gt;()</c>
/// so a hook type works whether configured at <c>AddDotnetCronner</c> or <c>app.UseDotnetCronner</c> time.
/// </summary>
internal sealed class ResolvedCronnerTaskHook : ICronnerTaskHook
{
    private readonly Type _hookType;

    public ResolvedCronnerTaskHook(Type hookType)
    {
        if (!typeof(ICronnerTaskHook).IsAssignableFrom(hookType))
            throw new ArgumentException($"{hookType} must implement {nameof(ICronnerTaskHook)}.", nameof(hookType));
        _hookType = hookType;
    }

    private Task Dispatch(CronnerHookEvent hookEvent, CronnerTaskContext context)
    {
        var hook = (ICronnerTaskHook)ActivatorUtilities.GetServiceOrCreateInstance(context.Services, _hookType);
        return CronnerHookInvoke.Dispatch(hook, hookEvent, context);
    }

    public Task OnStartAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.Start, context);
    public Task OnSuccessAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.Success, context);
    public Task OnFailAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.Fail, context);
    public Task OnCancelAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.Cancel, context);
    public Task OnLockAcquireAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.LockAcquire, context);
    public Task OnKeepAliveAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.KeepAlive, context);
    public Task OnLockReleaseAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.LockRelease, context);
    public Task OnLockLostAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.LockLost, context);
    public Task OnTotalProgressChangeAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.TotalProgressChange, context);
    public Task OnProgressScopeOpenedAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.ProgressScopeOpened, context);
    public Task OnScopeProgressAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.ScopeProgress, context);
    public Task OnProgressScopeClosedAsync(CronnerTaskContext context) => Dispatch(CronnerHookEvent.ProgressScopeClosed, context);
}
