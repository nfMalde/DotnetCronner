using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetCronner;

/// <summary>
/// Fires lifecycle and lock hooks for a task. Global hooks (builder-registered) and per-schedule hooks
/// each run in their own fresh DI scope; hooks registered directly in DI as <see cref="ICronnerTaskHook"/>
/// are resolved in one scope per event. A hook that throws is logged and swallowed so it never breaks
/// task execution. Registered as a singleton.
/// </summary>
public sealed class CronnerHookDispatcher
{
    private readonly CronnerHookRegistry _globalHooks;
    private readonly ILogger<CronnerHookDispatcher> _logger;

    /// <summary>Creates the dispatcher.</summary>
    public CronnerHookDispatcher(CronnerHookRegistry globalHooks, ILogger<CronnerHookDispatcher> logger)
    {
        _globalHooks = globalHooks;
        _logger = logger;
    }

    internal async Task DispatchAsync(
        CronnerHookEvent hookEvent,
        IServiceProvider rootProvider,
        CronnerJobDescriptor? descriptor,
        CronnerJob job,
        TimeSpan duration,
        Exception? exception,
        CancellationToken cancellationToken,
        decimal totalProgress = 0m,
        CronnerProgressInfo? progressScope = null)
    {
        // Global hooks, then per-schedule hooks — each in its own scope.
        foreach (var hook in _globalHooks.Hooks)
            await InvokeInOwnScopeAsync(hookEvent, hook, rootProvider, job, duration, exception, cancellationToken, totalProgress, progressScope).ConfigureAwait(false);

        if (descriptor is not null)
            foreach (var hook in descriptor.Hooks)
                await InvokeInOwnScopeAsync(hookEvent, hook, rootProvider, job, duration, exception, cancellationToken, totalProgress, progressScope).ConfigureAwait(false);

        // Hooks registered in DI as ICronnerTaskHook, resolved together in one dedicated scope.
        await using var scope = rootProvider.CreateAsyncScope();
        var diHooks = scope.ServiceProvider.GetServices<ICronnerTaskHook>();
        CronnerTaskContext? context = null;
        foreach (var hook in diHooks)
        {
            context ??= NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, totalProgress, progressScope);
            await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
        }
    }

    private async Task InvokeInOwnScopeAsync(
        CronnerHookEvent hookEvent, ICronnerTaskHook hook, IServiceProvider rootProvider,
        CronnerJob job, TimeSpan duration, Exception? exception, CancellationToken cancellationToken,
        decimal totalProgress, CronnerProgressInfo? progressScope)
    {
        await using var scope = rootProvider.CreateAsyncScope();
        var context = NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, totalProgress, progressScope);
        await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
    }

    private async Task SafeInvokeAsync(CronnerHookEvent hookEvent, ICronnerTaskHook hook, CronnerTaskContext context, string id)
    {
        try
        {
            await CronnerHookInvoke.Dispatch(hook, hookEvent, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DotnetCronner {HookEvent} hook failed for task {TaskId}.", hookEvent, id);
        }
    }

    private static CronnerTaskContext NewContext(
        IServiceProvider services, CronnerJob job, TimeSpan duration, Exception? exception,
        CancellationToken cancellationToken, decimal totalProgress, CronnerProgressInfo? progressScope) =>
        new()
        {
            Job = job,
            Services = services,
            CancellationToken = cancellationToken,
            Duration = duration,
            Exception = exception,
            TotalProgress = totalProgress,
            ProgressScope = progressScope,
        };
}
