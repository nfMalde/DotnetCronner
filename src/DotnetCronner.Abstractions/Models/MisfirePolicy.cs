namespace DotnetCronner;

/// <summary>
/// What to do about occurrences a task <em>missed</em> while the scheduler was unavailable (a "misfire") —
/// for example the scheduler was down, or paused past an occurrence. Set it per task (the
/// <c>[CronnerTask]</c> attribute or <c>WithMisfirePolicy(...)</c>) or globally via
/// <c>CronnerOptions.DefaultMisfirePolicy</c>.
/// </summary>
/// <remarks>
/// This is independent of <see cref="CronnerConcurrencyMode"/>. Concurrency governs what happens when a new
/// occurrence comes due <em>while a run is still executing</em> (in-flight overlap); a misfire policy governs
/// the <em>backlog</em> that built up while nothing was running. An occurrence is treated as a misfire only
/// once it is later than <c>CronnerOptions.MisfireThreshold</c>; a slightly-late normal fire is not a
/// misfire. Under <see cref="CronnerConcurrencyMode.Concurrent"/> a misfire always resolves to a single
/// catch-up (the schedule is advanced up front), regardless of the policy.
/// </remarks>
public enum MisfirePolicy
{
    /// <summary>
    /// Inherit <c>CronnerOptions.DefaultMisfirePolicy</c>. This is the value a task carries when it does
    /// not choose a policy of its own.
    /// </summary>
    Default = 0,

    /// <summary>Run one catch-up for the missed window, then resume at the next future occurrence.</summary>
    FireOnce = 1,

    /// <summary>Run none of the missed occurrences; resume at the next future occurrence.</summary>
    Skip = 2,

    /// <summary>
    /// Run every missed occurrence in order (sequentially, bounded by
    /// <c>CronnerOptions.MisfireCatchUpMax</c>), then resume at the next future occurrence.
    /// </summary>
    FireAll = 3,

    /// <summary>Do not catch up; resume at the next future occurrence. Behaves identically to <see cref="Skip"/>.</summary>
    FireNext = 4,
}
