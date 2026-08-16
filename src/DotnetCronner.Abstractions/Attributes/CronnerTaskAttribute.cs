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
}
