namespace DotnetCronner;

/// <summary>
/// Marks a method as a DotnetCronner task so it can be discovered and scheduled by assembly scanning.
/// All method parameters are resolved from dependency injection at execution time, with
/// <see cref="System.Threading.CancellationToken"/> parameters bound to the task's cancellation token.
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// public class ReportJobs
/// {
///     [CronnerTask(cronstring: "0 * * * *")]
///     public Task SendHourly(IReportService reports, CancellationToken ct) => reports.SendAsync(ct);
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CronnerTaskAttribute : Attribute
{
    /// <summary>
    /// Creates the attribute.
    /// </summary>
    /// <param name="id">
    /// A stable unique id for the task. When <c>null</c>, an id is generated deterministically from the
    /// declaring type and method name. Registering two tasks with the same id throws.
    /// </param>
    /// <param name="cronstring">
    /// The cron expression driving the schedule. When <c>null</c>, the task is registered as a manual /
    /// one-shot task that only runs when triggered via <see cref="ICronnerClient.ScheduleTaskAsync"/>.
    /// </param>
    public CronnerTaskAttribute(string? id = null, string? cronstring = null)
    {
        Id = id;
        CronString = cronstring;
    }

    /// <summary>The explicit task id, or <c>null</c> to auto-generate one.</summary>
    public string? Id { get; }

    /// <summary>The cron expression, or <c>null</c> for a manual task.</summary>
    public string? CronString { get; }

    /// <summary>The task's execution priority. Higher priorities are dispatched first. Defaults to <see cref="CronnerTaskPriority.Normal"/>.</summary>
    public CronnerTaskPriority Priority { get; set; } = CronnerTaskPriority.Normal;

    /// <summary>
    /// What happens when this task becomes due while a previous run is still executing. Defaults to
    /// <see cref="CronnerConcurrencyMode.DropAndForget"/> — a task never overlaps with itself.
    /// </summary>
    public CronnerConcurrencyMode Concurrency { get; set; } = CronnerConcurrencyMode.DropAndForget;

    /// <summary>An optional human-readable description, surfaced on <c>CronnerJobDescriptor</c> and admin listings.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// What to do about occurrences missed while the scheduler was unavailable. Defaults to
    /// <see cref="MisfirePolicy.Default"/> — inherit <c>CronnerOptions.DefaultMisfirePolicy</c>.
    /// </summary>
    public MisfirePolicy MisfirePolicy { get; set; } = MisfirePolicy.Default;

    /// <summary>
    /// Total attempts this task gets on failure, <em>including the first run</em> — <c>3</c> is one run plus
    /// two retries. Leave unset (<c>0</c>) to inherit <c>CronnerOptions.DefaultRetryPolicy</c>, and set it
    /// whenever you use any of the other retry properties: they describe a policy, and a policy needs an
    /// attempt count.
    /// </summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// How the wait before each retry is calculated. Defaults to <see cref="CronnerRetryStrategy.Default"/> —
    /// <see cref="CronnerRetryStrategy.Fixed"/> when <see cref="RetryDelaySeconds"/> is set, otherwise
    /// <see cref="CronnerRetryStrategy.Immediate"/>. Only meaningful together with <see cref="MaxAttempts"/>.
    /// </summary>
    public CronnerRetryStrategy RetryStrategy { get; set; } = CronnerRetryStrategy.Default;

    /// <summary>
    /// The base retry delay in seconds (an attribute cannot carry a <see cref="TimeSpan"/>): the wait itself
    /// under <see cref="CronnerRetryStrategy.Fixed"/>, the first wait under
    /// <see cref="CronnerRetryStrategy.Exponential"/>. Defaults to <c>0</c> — no wait.
    /// </summary>
    public double RetryDelaySeconds { get; set; }

    /// <summary>
    /// Caps any single retry delay, in seconds. Defaults to <c>0</c> — use <c>CronnerRetryPolicy.DefaultMaxDelay</c>
    /// (one hour). Mostly relevant to <see cref="CronnerRetryStrategy.Exponential"/>, where the wait keeps doubling.
    /// </summary>
    public double RetryMaxDelaySeconds { get; set; }

    /// <summary>
    /// Whether to spread the retry delay randomly (drawn from <c>[delay/2, delay]</c>) so that tasks failing
    /// together do not all retry at the same instant. Defaults to <c>false</c>. Composes with any strategy.
    /// </summary>
    public bool RetryJitter { get; set; }
}
