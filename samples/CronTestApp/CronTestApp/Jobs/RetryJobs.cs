using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// Retry policies in both flavours: <c>retry:flaky</c> configures one on the <c>[CronnerTask]</c> attribute
/// (plain properties — an attribute cannot carry a lambda or a <c>TimeSpan</c>), and <c>retry:exhausted</c>
/// is registered in <c>CronnerSetup</c> with the fluent <c>WithRetryPolicy(...)</c>. Both read their retry
/// context off <see cref="ICronnerJobContext"/>, so <c>GET /activity</c> shows the attempt ladder — including
/// the error the previous attempt failed with.
/// </summary>
public sealed class RetryJobs(JobActivityLog activity, ILogger<RetryJobs> logger)
{
    /// <summary>
    /// Fails every attempt but the last, so one occurrence walks the whole ladder and then succeeds.
    /// Exponential backoff from 2s with jitter: the waits are drawn from ~[1s, 2s] and ~[2s, 4s].
    /// </summary>
    [CronnerTask(id: "retry:flaky", cronstring: "0 */2 * * * *",
        MaxAttempts = 3, RetryStrategy = CronnerRetryStrategy.Exponential, RetryDelaySeconds = 2, RetryJitter = true)]
    public Task FlakyAsync(ICronnerJobContext context, CancellationToken cancellationToken)
    {
        Record("retry:flaky", context);

        if (context.Attempt < context.MaxAttempts)
            throw new InvalidOperationException($"transient failure on attempt {context.Attempt}");

        activity.Record("retry:flaky", $"attempt {context.Attempt} succeeded — the schedule resumes from here");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Always fails, so its attempts run out and the task is rescheduled at its next cron occurrence
    /// instead. Registered with the fluent builder in <c>CronnerSetup</c>.
    /// </summary>
    public Task AlwaysFailsAsync(ICronnerJobContext context, CancellationToken cancellationToken)
    {
        Record("retry:exhausted", context);
        throw new InvalidOperationException($"permanent failure on attempt {context.Attempt}");
    }

    // Attempt / MaxAttempts / PreviousError are the job's view of the retry context. PreviousError is the
    // message only — the exception object does not survive the wait between attempts, because a retry is
    // re-dispatched from the store (possibly by another instance).
    private void Record(string jobId, ICronnerJobContext context)
    {
        var previous = context.PreviousError is { } error ? $"; previous failure: {error}" : string.Empty;
        activity.Record(jobId, $"attempt {context.Attempt} of {context.MaxAttempts}{previous}");
        logger.LogInformation(
            "[{TaskId}] attempt {Attempt}/{MaxAttempts}, execution {ExecutionId}{Previous}",
            jobId, context.Attempt, context.MaxAttempts, context.ExecutionId[..8], previous);
    }
}
