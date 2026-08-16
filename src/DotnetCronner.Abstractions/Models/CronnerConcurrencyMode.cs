namespace DotnetCronner;

/// <summary>
/// Controls what happens when a task becomes due while a previous run of the <em>same id</em> is still
/// executing. The default guarantees a single concurrent run per task.
/// </summary>
public enum CronnerConcurrencyMode
{
    /// <summary>
    /// Skip the new occurrence — the task will simply run again at its next scheduled time. This is the
    /// default: a task never overlaps with itself and missed ticks are discarded.
    /// </summary>
    DropAndForget = 0,

    /// <summary>
    /// Run the missed occurrence immediately after the in-flight run finishes. Occurrences are processed
    /// one at a time in order, never overlapping.
    /// </summary>
    Queue = 1,

    /// <summary>
    /// Allow the new occurrence to run in parallel with the in-flight run.
    /// </summary>
    Concurrent = 2,
}
