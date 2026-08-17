namespace DotnetCronner;

/// <summary>Whether a <see cref="CronnerJob"/> is a recurring definition or a one-off enqueued instance.</summary>
public enum CronnerJobKind
{
    /// <summary>A recurring task driven by a cron expression, or a manual/one-shot definition triggered by id.</summary>
    Recurring = 0,

    /// <summary>
    /// A single enqueued instance carrying a payload, produced by <c>ICronnerClient.EnqueueAsync</c>. It runs
    /// once and is then subject to retention cleanup.
    /// </summary>
    OneOff = 1,
}
