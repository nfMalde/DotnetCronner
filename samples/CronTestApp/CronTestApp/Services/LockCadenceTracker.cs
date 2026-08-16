using System.Collections.Concurrent;

namespace CronTestApp.Services;

/// <summary>
/// Measures the gap between consecutive <c>OnKeepAlive</c> events per task, so the renewal cadence can be
/// checked against <c>LockTtl</c>/2 instead of taken on faith. Served from <c>GET /locks</c>.
/// </summary>
/// <remarks>
/// This is fed by the <em>first</em> registered global keepalive hook, so a deliberately slow hook behind
/// it (<c>CRONNER_SLOW_KEEPALIVE_MS</c>) cannot skew the measurement.
/// </remarks>
public sealed class LockCadenceTracker
{
    private readonly ConcurrentDictionary<string, Cadence> _byJob = new();

    /// <summary>
    /// Starts a new run for <paramref name="jobId"/> (called from <c>OnLockAcquire</c>). Gaps are only
    /// meaningful <em>within</em> one run — a job that gets one keepalive per run would otherwise appear to
    /// renew at "run duration + wait for the next tick" intervals.
    /// </summary>
    public void StartRun(string jobId) =>
        _byJob.GetOrAdd(jobId, static _ => new Cadence()).StartRun();

    /// <summary>Records that a keepalive fired for <paramref name="jobId"/> right now.</summary>
    public void Record(string jobId) =>
        _byJob.GetOrAdd(jobId, static _ => new Cadence()).Tick(DateTimeOffset.UtcNow);

    /// <summary>A JSON-friendly snapshot: the observed gaps and how they compare to the expected interval.</summary>
    public object Snapshot(TimeSpan lockTtl)
    {
        var expected = TimeSpan.FromMilliseconds(Math.Max(1000, lockTtl.TotalMilliseconds / 2));

        return new
        {
            lockTtl = lockTtl.ToString(),
            expectedKeepAliveInterval = expected.ToString(),
            // Only tasks that have actually been renewed at least once; everything else finishes well
            // inside one interval and has nothing to say about cadence.
            tasks = _byJob
                .Where(pair => pair.Value.KeepAlives > 0)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(expected)),
        };
    }

    private sealed class Cadence
    {
        private readonly object _gate = new();
        private readonly List<double> _gapsSeconds = [];
        private DateTimeOffset? _previous;
        private int _keepAlives;

        /// <summary>How many keepalives this task has seen since startup.</summary>
        public int KeepAlives
        {
            get { lock (_gate) { return _keepAlives; } }
        }

        public void StartRun()
        {
            lock (_gate)
            {
                _previous = null;   // never measure a gap across two runs
            }
        }

        public void Tick(DateTimeOffset at)
        {
            lock (_gate)
            {
                if (_previous is { } previous)
                    _gapsSeconds.Add((at - previous).TotalSeconds);

                _previous = at;
                _keepAlives++;
            }
        }

        public object Snapshot(TimeSpan expected)
        {
            lock (_gate)
            {
                if (_gapsSeconds.Count == 0)
                    return new
                    {
                        keepAlives = _keepAlives,
                        note = "one keepalive per run — no within-run gap to measure",
                    };

                return new
                {
                    keepAlives = _keepAlives,
                    gapsSeconds = _gapsSeconds.Select(gap => Math.Round(gap, 2)).ToArray(),
                    minSeconds = Math.Round(_gapsSeconds.Min(), 2),
                    maxSeconds = Math.Round(_gapsSeconds.Max(), 2),
                    averageSeconds = Math.Round(_gapsSeconds.Average(), 2),
                    // The whole point of the detached keepalive hook: this stays true no matter how slow
                    // the hook is, so the renewal never eats into the lock's TTL margin.
                    onCadence = _gapsSeconds.All(gap => Math.Abs(gap - expected.TotalSeconds) <= 2),
                };
            }
        }
    }
}
