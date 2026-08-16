using System.Collections.Concurrent;
using CronTestApp.ExternalJobs;

namespace CronTestApp.Services;

/// <summary>One line of job activity, served from <c>GET /activity</c>.</summary>
/// <param name="At">When it happened (UTC).</param>
/// <param name="JobId">The task id that produced it.</param>
/// <param name="Message">What happened.</param>
public sealed record JobActivityEntry(DateTimeOffset At, string JobId, string Message);

/// <summary>
/// A bounded, in-memory ring buffer of what the jobs did. This is the main way to <em>see</em> the
/// scheduler working — cron ticks, concurrency behaviour, retries and cancellations all land here.
/// </summary>
/// <remarks>
/// Registered as a singleton, and as the same instance in the dedicated job container, so the endpoints
/// show job activity in both <c>CRONNER_JOB_SERVICES</c> modes.
/// </remarks>
public sealed class JobActivityLog : IJobActivitySink
{
    private const int Capacity = 500;

    private readonly ConcurrentQueue<JobActivityEntry> _entries = new();
    private readonly ConcurrentDictionary<string, int> _countsByJob = new();

    /// <inheritdoc />
    public void Record(string jobId, string message)
    {
        _entries.Enqueue(new JobActivityEntry(DateTimeOffset.UtcNow, jobId, message));
        _countsByJob.AddOrUpdate(jobId, 1, static (_, count) => count + 1);

        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<JobActivityEntry> Recent(int take = 50, string? jobId = null) =>
        _entries
            .Reverse()
            .Where(entry => jobId is null || entry.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(take, 1, Capacity))
            .ToArray();

    /// <summary>How many entries each task has produced since startup.</summary>
    public IReadOnlyDictionary<string, int> CountsByJob() =>
        _countsByJob.ToDictionary(pair => pair.Key, pair => pair.Value).AsReadOnly();
}
