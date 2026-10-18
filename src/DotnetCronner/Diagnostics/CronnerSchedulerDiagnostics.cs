using System.Collections.Concurrent;

namespace DotnetCronner;

/// <summary>
/// Live state of the scheduler in this process: whether it started, when it last polled the store for due
/// work, and how many runs are in flight. Registered as a singleton and updated by the scheduler itself.
/// </summary>
/// <remarks>
/// This is what the <c>cronner.jobs.running</c> metric observes and what the health checks in
/// <c>DotnetCronner.AspNetCore</c> read, but it is public so you can expose the same facts on your own
/// admin surface. Everything here is a read-only observation; only the scheduler mutates it.
/// </remarks>
public sealed class CronnerSchedulerDiagnostics
{
    private long _startedTicks;
    private long _lastPollTicks;
    private long _pollCount;
    private long _lastPollFaultTicks;
    private string? _lastPollError;

    // Start time per in-flight run, keyed by execution id — so concurrent runs of one task each count, and a
    // stalled-execution check can ask how long the oldest one has been going.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _running = new();

    /// <summary>Whether the scheduler's hosted service has started.</summary>
    public bool IsStarted => Interlocked.Read(ref _startedTicks) != 0;

    /// <summary>When the scheduler started, or <c>null</c> if it has not.</summary>
    public DateTimeOffset? StartedUtc => Read(ref _startedTicks);

    /// <summary>
    /// When the scheduler last completed a poll of the store for due work. A value that stops advancing
    /// while the scheduler is started means the poll loop is wedged — which is exactly what the
    /// "scheduler is processing due work" health check watches.
    /// </summary>
    public DateTimeOffset? LastPollUtc => Read(ref _lastPollTicks);

    /// <summary>How many polls have completed since start.</summary>
    public long PollCount => Interlocked.Read(ref _pollCount);

    /// <summary>How many executions this instance is running right now (concurrent runs counted individually).</summary>
    public int RunningCount => _running.Count;

    /// <summary>
    /// How long the longest-running execution on this instance has been going, or <c>null</c> when nothing is
    /// running. A value far past what your jobs should take is the signal a stalled-execution check watches.
    /// </summary>
    public TimeSpan? LongestRunningDuration
    {
        get
        {
            var oldest = default(DateTimeOffset?);
            foreach (var startedAt in _running.Values)
            {
                if (oldest is null || startedAt < oldest)
                    oldest = startedAt;
            }

            return oldest is { } value ? DateTimeOffset.UtcNow - value : null;
        }
    }

    /// <summary>When a poll last failed, or <c>null</c> if none has.</summary>
    public DateTimeOffset? LastPollFaultUtc => Read(ref _lastPollFaultTicks);

    /// <summary>The message of the most recent poll failure, or <c>null</c> if none has occurred.</summary>
    public string? LastPollError => Volatile.Read(ref _lastPollError);

    internal void MarkStarted() => Interlocked.Exchange(ref _startedTicks, DateTimeOffset.UtcNow.UtcTicks);

    internal void MarkPolled()
    {
        Interlocked.Exchange(ref _lastPollTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Increment(ref _pollCount);
    }

    // A poll that threw still counts as the loop being alive — the store, not the scheduler, is the thing
    // that failed, and the two are reported separately so one does not mask the other.
    internal void MarkPollFaulted(Exception exception)
    {
        Interlocked.Exchange(ref _lastPollFaultTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastPollError, exception.Message);
        Interlocked.Exchange(ref _lastPollTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    internal void RunStarted(string executionId) => _running[executionId] = DateTimeOffset.UtcNow;

    internal void RunFinished(string executionId) => _running.TryRemove(executionId, out _);

    private static DateTimeOffset? Read(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}
