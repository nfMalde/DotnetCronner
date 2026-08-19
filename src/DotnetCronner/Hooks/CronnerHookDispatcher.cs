using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly CronnerOptions _options;

    /// <summary>Creates the dispatcher.</summary>
    public CronnerHookDispatcher(CronnerHookRegistry globalHooks, ILogger<CronnerHookDispatcher> logger, IOptions<CronnerOptions> options)
    {
        _globalHooks = globalHooks;
        _logger = logger;
        _options = options.Value;
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
        CronnerProgressInfo? progressScope = null,
        CronnerRunState? runState = null,
        bool willRetry = false,
        object? progressPayload = null)
    {
        // Global hooks, then per-schedule hooks — each in its own scope.
        foreach (var hook in _globalHooks.Hooks)
            await InvokeInOwnScopeAsync(hookEvent, hook, rootProvider, job, duration, exception, cancellationToken, totalProgress, progressScope, runState, willRetry, progressPayload).ConfigureAwait(false);

        if (descriptor is not null)
            foreach (var hook in descriptor.Hooks)
                await InvokeInOwnScopeAsync(hookEvent, hook, rootProvider, job, duration, exception, cancellationToken, totalProgress, progressScope, runState, willRetry, progressPayload).ConfigureAwait(false);

        // Hooks registered in DI as ICronnerTaskHook, resolved together in one dedicated scope.
        await using var scope = rootProvider.CreateAsyncScope();
        var diHooks = scope.ServiceProvider.GetServices<ICronnerTaskHook>();
        CronnerTaskContext? context = null;
        foreach (var hook in diHooks)
        {
            context ??= NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, totalProgress, progressScope, runState, willRetry, progressPayload);
            await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
        }
    }

    private async Task InvokeInOwnScopeAsync(
        CronnerHookEvent hookEvent, ICronnerTaskHook hook, IServiceProvider rootProvider,
        CronnerJob job, TimeSpan duration, Exception? exception, CancellationToken cancellationToken,
        decimal totalProgress, CronnerProgressInfo? progressScope, CronnerRunState? runState, bool willRetry,
        object? progressPayload)
    {
        await using var scope = rootProvider.CreateAsyncScope();
        var context = NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, totalProgress, progressScope, runState, willRetry, progressPayload);
        await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a terminal lifecycle event (Start/Success/Fail/Cancel) routing <em>each hook</em> by its
    /// effective scope: a hook's own <see cref="ICronnerScopedHook.PreferredScope"/> if it set one (via
    /// <c>AddHook</c>/<c>WithHook</c>), otherwise <paramref name="defaultScope"/>. Shared hooks run in the
    /// job's scope (<paramref name="jobScope"/>) so <c>ctx.HasParam&lt;T&gt;()</c> resolves the job's scoped
    /// instances; isolated hooks each run in their own fresh scope off <paramref name="rootProvider"/>.
    /// DI-registered hooks carry no per-hook preference, so they follow <paramref name="defaultScope"/>.
    /// </summary>
    internal async Task DispatchTerminalAsync(
        CronnerHookEvent hookEvent, IServiceProvider jobScope, IServiceProvider rootProvider,
        CronnerJobDescriptor? descriptor, CronnerJob job, TimeSpan duration, Exception? exception,
        CancellationToken cancellationToken, CronnerHookScope defaultScope, CronnerRunState? runState, bool willRetry)
    {
        CronnerTaskContext? sharedContext = null;
        CronnerTaskContext Shared() => sharedContext ??=
            NewContext(jobScope, job, duration, exception, cancellationToken, 0m, null, runState, willRetry);

        async Task RunAsync(ICronnerTaskHook hook)
        {
            var effective = (hook as ICronnerScopedHook)?.PreferredScope ?? defaultScope;
            if (effective == CronnerHookScope.Isolated)
            {
                await using var scope = rootProvider.CreateAsyncScope();
                var context = NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, 0m, null, runState, willRetry);
                await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
            }
            else
            {
                await SafeInvokeAsync(hookEvent, hook, Shared(), job.Id).ConfigureAwait(false);
            }
        }

        foreach (var hook in _globalHooks.Hooks)
            await RunAsync(hook).ConfigureAwait(false);

        if (descriptor is not null)
            foreach (var hook in descriptor.Hooks)
                await RunAsync(hook).ConfigureAwait(false);

        // DI-registered ICronnerTaskHook instances have no per-hook preference — they follow the default.
        if (defaultScope == CronnerHookScope.Isolated)
        {
            await using var scope = rootProvider.CreateAsyncScope();
            CronnerTaskContext? context = null;
            foreach (var hook in scope.ServiceProvider.GetServices<ICronnerTaskHook>())
            {
                context ??= NewContext(scope.ServiceProvider, job, duration, exception, cancellationToken, 0m, null, runState, willRetry);
                await SafeInvokeAsync(hookEvent, hook, context, job.Id).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var hook in jobScope.GetServices<ICronnerTaskHook>())
                await SafeInvokeAsync(hookEvent, hook, Shared(), job.Id).ConfigureAwait(false);
        }
    }

    private async Task SafeInvokeAsync(CronnerHookEvent hookEvent, ICronnerTaskHook hook, CronnerTaskContext context, string id)
    {
        // A scope-carrying wrapper is never invoked itself — dispatch to the hook it wraps.
        var target = hook is ScopedHookWrapper wrapper ? wrapper.Inner : hook;
        try
        {
            await CronnerHookInvoke.Dispatch(target, hookEvent, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DotnetCronner {HookEvent} hook failed for task {TaskId}.", hookEvent, id);
        }
    }

    private CronnerTaskContext NewContext(
        IServiceProvider services, CronnerJob job, TimeSpan duration, Exception? exception,
        CancellationToken cancellationToken, decimal totalProgress, CronnerProgressInfo? progressScope,
        CronnerRunState? runState, bool willRetry, object? progressPayload = null) =>
        new()
        {
            Job = job,
            Services = services,
            CancellationToken = cancellationToken,
            Duration = duration,
            Exception = exception,
            TotalProgress = totalProgress,
            ProgressScope = progressScope,
            RunState = runState,
            WillRetry = willRetry,
            ProgressPayload = progressPayload,
            LockTtl = _options.LockTtl,
            KeepAliveInterval = _options.EffectiveKeepAliveInterval(),
        };
}
