using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Hooks;

/// <summary>
/// An <see cref="ICronnerTaskHook"/> resolved from DI per invocation — the reusable-class style, as
/// opposed to the delegate and expression hooks registered fluently in
/// <see cref="Configuration.CronnerSetup"/>. It implements <em>all twelve</em> events and owns the
/// <c>/metrics</c> and <c>/progress</c> data, so those endpoints are proof the hooks fired.
/// </summary>
/// <remarks>
/// Every hook method runs in its own fresh DI scope; this class demonstrates both ways of reaching it —
/// constructor injection (below) and <c>context.HasParam&lt;T&gt;()</c> inside a method.
/// </remarks>
public sealed class LoggingHook(
    JobMetrics metrics, JobActivityLog activity, JobProgressTracker progress, ILogger<LoggingHook> logger)
    : ICronnerTaskHook
{
    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task OnStartAsync(CronnerTaskContext context)
    {
        metrics.Started(context.Job.Id);
        logger.LogDebug("[hook] starting {TaskId}", context.Job.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnSuccessAsync(CronnerTaskContext context)
    {
        metrics.Succeeded(context.Job.Id, context.Duration);
        logger.LogInformation("[hook] {TaskId} succeeded in {Duration}", context.Job.Id, context.Duration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnFailAsync(CronnerTaskContext context)
    {
        metrics.Failed(context.Job.Id, context.Duration, context.Exception?.Message);
        activity.Record(context.Job.Id, $"[hook] failed after {context.Duration}: {context.Exception?.Message}");
        logger.LogError(context.Exception, "[hook] {TaskId} failed after {Duration}", context.Job.Id, context.Duration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnCancelAsync(CronnerTaskContext context)
    {
        metrics.Cancelled(context.Job.Id);
        activity.Record(context.Job.Id, "[hook] cancelled");
        logger.LogWarning("[hook] {TaskId} was cancelled after {Duration}", context.Job.Id, context.Duration);
        return Task.CompletedTask;
    }

    // ── Execution lock ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task OnLockAcquireAsync(CronnerTaskContext context)
    {
        metrics.Lock(context.Job.Id, JobMetrics.LockEvent.Acquired);
        activity.Record(context.Job.Id, $"[lock] acquired until {context.Job.LockedUntilUtc:HH:mm:ss} by {Short(context.Job.LockOwner)}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>context.Job</c> is the snapshot taken when the claim was made, so <c>LockedUntilUtc</c> here is
    /// still the <em>original</em> expiry — the renewed value lives in the store, not in this context.
    /// </remarks>
    public Task OnKeepAliveAsync(CronnerTaskContext context)
    {
        metrics.Lock(context.Job.Id, JobMetrics.LockEvent.KeptAlive);
        activity.Record(
            context.Job.Id,
            $"[lock] keepalive — claim renewed in the store (context snapshot still reads {context.Job.LockedUntilUtc:HH:mm:ss}Z)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnLockReleaseAsync(CronnerTaskContext context)
    {
        metrics.Lock(context.Job.Id, JobMetrics.LockEvent.Released);
        activity.Record(context.Job.Id, "[lock] released");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnLockLostAsync(CronnerTaskContext context)
    {
        metrics.Lock(context.Job.Id, JobMetrics.LockEvent.Lost);
        activity.Record(
            context.Job.Id,
            $"[lock] LOST — a keepalive found the claim gone, run stood down " +
            $"(context still shows the old owner {Short(context.Job.LockOwner)})");
        logger.LogWarning("[hook] {TaskId} lost its execution lock mid-run", context.Job.Id);
        return Task.CompletedTask;
    }

    // ── Progress ─────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task OnTotalProgressChangeAsync(CronnerTaskContext context)
    {
        // Resolved from this hook invocation's own scope rather than the constructor, to exercise HasParam.
        // context.ProgressPayload is the per-report custom payload the job passed to Progress(value, payload).
        context.HasParam<JobProgressTracker>().Total(context.Job.Id, context.TotalProgress, context.ProgressPayload as string);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnProgressScopeOpenedAsync(CronnerTaskContext context)
    {
        progress.ScopeOpened(context.Job.Id, context.ProgressScope!);
        activity.Record(context.Job.Id, $"[progress] scope '{context.ProgressScope!.Category}' opened");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnScopeProgressAsync(CronnerTaskContext context)
    {
        progress.ScopeProgress(context.Job.Id, context.ProgressScope!, context.ProgressPayload as string);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnProgressScopeClosedAsync(CronnerTaskContext context)
    {
        progress.ScopeClosed(context.Job.Id, context.ProgressScope!);
        activity.Record(
            context.Job.Id,
            $"[progress] scope '{context.ProgressScope!.Category}' closed at {context.ProgressScope!.Value:P0} (total {context.TotalProgress:P0})");
        return Task.CompletedTask;
    }

    private static string Short(string? lockOwner) =>
        string.IsNullOrEmpty(lockOwner) ? "(nobody)" : lockOwner[..Math.Min(8, lockOwner.Length)];
}
