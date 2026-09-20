namespace DotnetCronner;

/// <summary>
/// How long to wait before the next attempt when a task fails and the retry policy still allows one. Set it
/// per task (the <c>[CronnerTask]</c> attribute or <c>WithRetryPolicy(...)</c>) or globally via
/// <c>CronnerOptions.DefaultRetryPolicy</c>.
/// </summary>
/// <remarks>
/// Jitter is <em>not</em> a strategy — it is a modifier that composes with any of these (<c>WithJitter()</c>),
/// so "exponential backoff with jitter" is <see cref="Exponential"/> plus jitter. A retry never becomes a new
/// cron occurrence: it is re-dispatched as its own execution with its own id and an incremented attempt
/// number, and the cron schedule only resumes once the attempts are exhausted or one of them succeeds.
/// </remarks>
public enum CronnerRetryStrategy
{
    /// <summary>
    /// Inherit — the value a task carries when it does not choose a strategy of its own. Resolves to
    /// <see cref="Fixed"/> when a delay is configured and <see cref="Immediate"/> when it is not.
    /// </summary>
    Default = 0,

    /// <summary>Retry as soon as the scheduler picks the task up again — no wait between attempts.</summary>
    Immediate = 1,

    /// <summary>Wait the same configured delay before every attempt.</summary>
    Fixed = 2,

    /// <summary>
    /// Double the wait after each failed attempt (<c>delay</c>, <c>2×delay</c>, <c>4×delay</c>, …), capped at
    /// the policy's maximum delay so a long outage cannot push the next attempt arbitrarily far out.
    /// </summary>
    Exponential = 3,
}
