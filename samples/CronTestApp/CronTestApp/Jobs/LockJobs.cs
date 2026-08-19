using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// Everything around the execution lock and cancellation: the keepalive that stops a long run from being
/// mistaken for a stalled worker, losing the lock mid-run, and cooperative cancellation.
/// </summary>
/// <remarks>
/// All of these are manual tasks so you can drive them from <c>CronTestApp.http</c> and watch
/// <c>GET /activity</c>. Keep <c>CRONNER_LOCK_TTL_SECONDS</c> small (the default here is 20s, so the
/// keepalive renews every 10s) or these runs finish before anything interesting happens.
/// </remarks>
public sealed class LockJobs(JobActivityLog activity, ICronnerJobContext jobContext, ILogger<LockJobs> logger)
{
    /// <summary>
    /// Runs for ~35s, i.e. longer than <c>LockTtl</c>, so the claim has to be renewed to survive. Expect
    /// `OnLockAcquire`, several `OnKeepAlive`s, then `OnLockRelease` in <c>/activity</c>.
    /// </summary>
    /// <remarks>
    /// It also shows the consumer use case behind <see cref="ICronnerJobContext.ExecutionId"/>: a per-run
    /// log file named after the execution id — the same id every hook of this run sees
    /// (<c>CronnerTaskContext.ExecutionId</c>) and the history row is keyed by, so your own per-run
    /// artefact and the scheduler's record join without a second table.
    /// </remarks>
    [CronnerTask(id: "lock:keepalive")]
    public async Task LongRunAsync(CancellationToken cancellationToken)
    {
        var executionId = jobContext.ExecutionId;
        var logFile = Path.Combine(Path.GetTempPath(), "crontestapp", $"lock-keepalive-{executionId}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        activity.Record("lock:keepalive", $"started (execution {executionId[..8]}) — outliving LockTtl, so the keepalive must renew the claim; per-run log: {logFile}");

        for (var second = 1; second <= 35; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await File.AppendAllTextAsync(logFile, $"{DateTimeOffset.UtcNow:O} still running after {second}s{Environment.NewLine}", cancellationToken);
            if (second % 10 == 0)
                activity.Record("lock:keepalive", $"still running after {second}s (claim renewed by the keepalive)");
        }

        activity.Record("lock:keepalive", $"finished with its claim intact (execution {executionId[..8]})");
    }

    /// <summary>
    /// The lease rule. Start it, then <c>POST /store/renewal-outage?seconds=N</c> (custom JSON store only) to
    /// make every renewal THROW for N seconds: a short outage (a few seconds under a 20s TTL) is survived — the
    /// engine retries while the last confirmed expiry is still ahead — while a long one makes the engine abandon
    /// the run <em>before</em> the lease lapses (<c>OnLockLost</c>, history row <c>Cancelled</c> with
    /// <c>CronnerExecutionErrors.LockLost</c>), so another instance can never overlap with it.
    /// </summary>
    [CronnerTask(id: "lock:outage")]
    public async Task OutageAsync(CancellationToken cancellationToken)
    {
        activity.Record("lock:outage", "started — POST /store/renewal-outage?seconds=N to make renewals throw (short: survives, long: abandoned before the lease lapses)");
        try
        {
            for (var second = 1; second <= 90; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (second % 10 == 0)
                    activity.Record("lock:outage", $"still running after {second}s");
            }
        }
        catch (OperationCanceledException)
        {
            activity.Record("lock:outage", "run cancelled — the lease could not be confirmed in time, so this execution stood down before it could lapse");
            throw;
        }

        activity.Record("lock:outage", "finished — every renewal was confirmed (or the outage was short enough)");
    }

    /// <summary>
    /// The same long run, but meant to have its claim taken away with
    /// <c>POST /tasks/lock:stealable/steal-lock</c> (which releases the lock on behalf of its owner, as a
    /// reclaim by another node would): the next keepalive is refused, the scheduler cancels this run to
    /// prevent a double execution, and <c>OnLockLost</c> fires.
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
