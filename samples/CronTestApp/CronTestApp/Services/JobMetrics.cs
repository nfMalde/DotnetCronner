using System.Collections.Concurrent;

namespace CronTestApp.Services;

/// <summary>Per-task counters, filled exclusively by the lifecycle hooks and served from <c>GET /metrics</c>.</summary>
public sealed class JobMetrics
{
    private readonly ConcurrentDictionary<string, Counters> _byJob = new();

    /// <summary>Number of executions currently in flight, across all tasks.</summary>
    public int Running => _byJob.Values.Sum(c => c.Running);

    /// <summary>Records a started execution (from <c>OnStart</c>).</summary>
    public void Started(string jobId)
    {
        var counters = _byJob.GetOrAdd(jobId, static _ => new Counters());
        Interlocked.Increment(ref counters.StartedCount);
        Interlocked.Increment(ref counters.RunningCount);
    }

    /// <summary>Records a successful execution and how long it took (from <c>OnSuccess</c>).</summary>
    public void Succeeded(string jobId, TimeSpan duration)
    {
        var counters = _byJob.GetOrAdd(jobId, static _ => new Counters());
        Interlocked.Increment(ref counters.SucceededCount);
        Interlocked.Decrement(ref counters.RunningCount);
        counters.RecordDuration(duration);
    }

    /// <summary>Records a failed execution (from <c>OnFail</c>).</summary>
    public void Failed(string jobId, TimeSpan duration, string? error)
    {
        var counters = _byJob.GetOrAdd(jobId, static _ => new Counters());
        Interlocked.Increment(ref counters.FailedCount);
        Interlocked.Decrement(ref counters.RunningCount);
        counters.RecordDuration(duration);
        counters.LastError = error;
    }

    /// <summary>Records a cancelled execution (from <c>OnCancel</c>).</summary>
    public void Cancelled(string jobId)
    {
        var counters = _byJob.GetOrAdd(jobId, static _ => new Counters());
        Interlocked.Increment(ref counters.CancelledCount);
        Interlocked.Decrement(ref counters.RunningCount);
    }

    /// <summary>Records a lock event (from <c>OnLockAcquire</c> / <c>OnKeepAlive</c> / <c>OnLockRelease</c> / <c>OnLockLost</c>).</summary>
    public void Lock(string jobId, LockEvent lockEvent)
    {
        var counters = _byJob.GetOrAdd(jobId, static _ => new Counters());
        switch (lockEvent)
        {
            case LockEvent.Acquired: Interlocked.Increment(ref counters.LockAcquiredCount); break;
            case LockEvent.KeptAlive: Interlocked.Increment(ref counters.KeepAliveCount); break;
            case LockEvent.Released: Interlocked.Increment(ref counters.LockReleasedCount); break;
            case LockEvent.Lost: Interlocked.Increment(ref counters.LockLostCount); break;
        }
    }

    /// <summary>The execution-lock events reported by the lock hooks.</summary>
    public enum LockEvent
    {
        /// <summary>The claim was taken.</summary>
        Acquired,

        /// <summary>The claim was renewed by the keepalive.</summary>
        KeptAlive,

        /// <summary>The claim was released.</summary>
        Released,

        /// <summary>The claim was lost mid-run.</summary>
        Lost,
    }

    /// <summary>A JSON-friendly snapshot of all counters.</summary>
    public object Snapshot() => new
    {
        running = Running,
        tasks = _byJob
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
    };

    private sealed class Counters
    {
        public int StartedCount;
        public int SucceededCount;
        public int FailedCount;
        public int CancelledCount;
        public int RunningCount;
        public int LockAcquiredCount;
        public int KeepAliveCount;
        public int LockReleasedCount;
        public int LockLostCount;

        private long _totalDurationTicks;

        public string? LastError { get; set; }

        public int Running => Volatile.Read(ref RunningCount);

        public void RecordDuration(TimeSpan duration) =>
            Interlocked.Add(ref _totalDurationTicks, duration.Ticks);

        public object Snapshot()
        {
            var completed = Volatile.Read(ref SucceededCount) + Volatile.Read(ref FailedCount);
            var totalTicks = Interlocked.Read(ref _totalDurationTicks);

            return new
            {
                started = Volatile.Read(ref StartedCount),
                succeeded = Volatile.Read(ref SucceededCount),
                failed = Volatile.Read(ref FailedCount),
                cancelled = Volatile.Read(ref CancelledCount),
                running = Running,
                averageDuration = completed == 0
                    ? null
                    : TimeSpan.FromTicks(totalTicks / completed).ToString(),
                lastError = LastError,
                locks = new
                {
                    acquired = Volatile.Read(ref LockAcquiredCount),
                    keepAlives = Volatile.Read(ref KeepAliveCount),
                    released = Volatile.Read(ref LockReleasedCount),
                    lost = Volatile.Read(ref LockLostCount),
                },
            };
        }
    }
}
