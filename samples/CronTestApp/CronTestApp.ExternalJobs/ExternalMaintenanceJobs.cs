using DotnetCronner;
using Microsoft.Extensions.Logging;

namespace CronTestApp.ExternalJobs;

/// <summary>
/// Attribute tasks that live <em>outside</em> the entry assembly. They are only registered when the app
/// calls <c>AutoDiscoverFromAssembly(typeof(ExternalMaintenanceJobs))</c> — entry-assembly scanning alone
/// never finds them.
/// </summary>
public sealed class ExternalMaintenanceJobs(IJobActivitySink activity, ILogger<ExternalMaintenanceJobs> logger)
{
    /// <summary>Discovered from another assembly, scheduled with a plain 5-field cron.</summary>
    [CronnerTask(id: "external:cleanup", cronstring: "*/2 * * * *", Priority = CronnerTaskPriority.Low)]
    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("external:cleanup ran from {Assembly}", typeof(ExternalMaintenanceJobs).Assembly.GetName().Name);
        activity.Record("external:cleanup", "cleaned up temp data (job class lives in CronTestApp.ExternalJobs)");
        return Task.CompletedTask;
    }
}

/// <summary>
/// An external job that also implements the discovery marker, so it survives the <c>filtered</c>
/// discovery mode.
/// </summary>
public sealed class ExternalMarkerJobs(IJobActivitySink activity) : IScheduledJob
{
    /// <summary>Discovered from another assembly <em>and</em> assignable to <see cref="IScheduledJob"/>.</summary>
    [CronnerTask(id: "external:marker", cronstring: "*/20 * * * * *")]
    public void Ping() => activity.Record("external:marker", "external job implementing IScheduledJob");
}
