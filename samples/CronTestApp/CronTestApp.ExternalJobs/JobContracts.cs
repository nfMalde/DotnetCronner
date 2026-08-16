namespace CronTestApp.ExternalJobs;

/// <summary>
/// Marker interface used to test <c>AutoDiscoverFromType(typeof(IScheduledJob))</c>: with the
/// <c>filtered</c> discovery mode only job classes implementing this interface are discovered.
/// It lives in this assembly so job classes in <em>both</em> assemblies can implement it.
/// </summary>
public interface IScheduledJob;

/// <summary>
/// The activity log, seen from a job's point of view. The web app implements this so jobs in this
/// separate assembly can report what they did without referencing the app.
/// </summary>
public interface IJobActivitySink
{
    /// <summary>Records one line of job activity.</summary>
    void Record(string jobId, string message);
}
