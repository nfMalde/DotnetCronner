using CronTestApp.ExternalJobs;
using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// Implements the <see cref="IScheduledJob"/> marker, so it is the only job class in this assembly that
/// survives <c>CRONNER_DISCOVERY=filtered</c> (which adds
/// <c>AutoDiscoverFromType(typeof(IScheduledJob))</c>).
/// </summary>
public sealed class MarkerJobs(JobActivityLog activity) : IScheduledJob
{
    /// <summary>Discovered in every mode except <c>off</c>.</summary>
    [CronnerTask(id: "marker:filtered", cronstring: "*/20 * * * * *")]
    public void Filtered() =>
        activity.Record("marker:filtered", "type filter hit — class implements IScheduledJob");
}
