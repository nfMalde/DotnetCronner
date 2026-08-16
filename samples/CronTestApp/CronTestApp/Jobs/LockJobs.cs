using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// Everything around the execution lock and cancellation: the keepalive that stops a long run from being
/// mistaken for a stalled worker, losing the lock mid-run, and cooperative cancellation.
/// </summary>
/// <remarks>
/// All three are manual tasks so you can drive them from <c>CronTestApp.http</c> and watch
/// <c>GET /activity</c>. Keep <c>CRONNER_LOCK_TTL_SECONDS</c> small (the default here is 20s, so the
/// keepalive renews every 10s) or these runs finish before anything interesting happens.
/// </remarks>
public sealed class LockJobs(JobActivityLog activity, ILogger<LockJobs> logger)
{
    /// <summary>
    /// Runs for ~35s, i.e. longer than <c>LockTtl</c>, so the claim has to be renewed to survive. Expect
    /// `OnLockAcquire`, several `OnKeepAlive`s, then `OnLockRelease` in <c>/activity</c>.
    /// </summary>
    [CronnerTask(id: "lock:keepalive")]
    public async Task LongRunAsync(CancellationToken cancellationToken)
    {
        activity.Record("lock:keepalive", "started — outliving LockTtl, so the keepalive must renew the claim");

        for (var second = 1; second <= 35; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (second % 10 == 0)
                activity.Record("lock:keepalive", $"still running after {second}s (claim renewed by the keepalive)");
        }

        activity.Record("lock:keepalive", "finished with its claim intact");
    }

    /// <summary>
    /// The same long run, but meant to have its lock stolen with
    /// <c>POST /tasks/lock:stealable/steal-lock</c>: the next keepalive fails, the scheduler cancels this
    /// run to prevent a double execution, and <c>OnLockLost</c> fires.
    /// </summary>
    [CronnerTask(id: "lock:stealable")]
    public async Task StealableAsync(CancellationToken cancellationToken)
    {
        activity.Record("lock:stealable", "started — POST /tasks/lock:stealable/steal-lock to take its claim away");

        try
        {
            for (var second = 1; second <= 60; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (second % 10 == 0)
                    activity.Record("lock:stealable", $"still holding the claim after {second}s");
            }
        }
        catch (OperationCanceledException)
        {
            // The scheduler cancels the run when a keepalive discovers the lock is gone.
            activity.Record("lock:stealable", "run cancelled — the lock was lost, so this execution stood down");
            throw;
        }

        activity.Record("lock:stealable", "finished — nobody stole the lock");
    }

    /// <summary>
    /// Cooperative cancellation: start it with <c>POST /tasks/cancel:long-runner/run</c>, stop it with
    /// <c>POST /tasks/cancel:long-runner/cancel</c>, and watch `OnCancel` fire.
    /// </summary>
    /// <remarks>Cancelling also unschedules the task, so it stays <c>Cancelled</c> until you trigger it again.</remarks>
    [CronnerTask(id: "cancel:long-runner")]
    public async Task CancellableAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("cancel:long-runner started");
        activity.Record("cancel:long-runner", "started — POST /tasks/cancel:long-runner/cancel to stop it");

        for (var second = 1; second <= 300; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (second % 10 == 0)
                activity.Record("cancel:long-runner", $"still running after {second}s");
        }

        activity.Record("cancel:long-runner", "ran to completion without being cancelled");
    }
}
