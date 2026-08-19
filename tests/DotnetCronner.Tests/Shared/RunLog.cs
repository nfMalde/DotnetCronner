using System.Collections.Concurrent;
using Shouldly;

namespace DotnetCronner.Tests.Shared;

/// <summary>One recorded job run: which task, which execution, on which scheduler instance, and when.</summary>
public sealed record RunRecord(string TaskId, string ExecutionId, string Host, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// A thread-safe log the recording jobs of several hosts write into, plus the assertions that make up the
/// "one run per task across processes" evidence: every run has a distinct execution id, and no two runs of the
/// same task overlap in time.
/// </summary>
public sealed class RunLog
{
    private readonly ConcurrentBag<RunRecord> _records = [];
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.Ordinal);
    private int _overlaps;
    private int _started;

    public IReadOnlyCollection<RunRecord> Records => _records.ToArray();

    /// <summary>Finished runs.</summary>
    public int Count => _records.Count;

    /// <summary>Runs that have started (finished or not).</summary>
    public int Started => Volatile.Read(ref _started);

    /// <summary>How many times a run started while another run of the same task was still in progress.</summary>
    public int Overlaps => Volatile.Read(ref _overlaps);

    public IDisposable Begin(string taskId, string executionId, string host)
    {
        Interlocked.Increment(ref _started);
        // Live detection, independent of clock resolution: bump the per-task active counter and flag any > 1.
        if (_active.AddOrUpdate(taskId, 1, (_, n) => n + 1) > 1)
            Interlocked.Increment(ref _overlaps);
        return new Scope(this, taskId, executionId, host, DateTimeOffset.UtcNow);
    }

    private void End(string taskId, string executionId, string host, DateTimeOffset start)
    {
        _active.AddOrUpdate(taskId, 0, (_, n) => n - 1);
        _records.Add(new RunRecord(taskId, executionId, host, start, DateTimeOffset.UtcNow));
    }

    /// <summary>Asserts the core guarantee over everything recorded so far.</summary>
    public void ShouldShowNoConcurrentRunsOfTheSameTask()
    {
        Overlaps.ShouldBe(0, "a run of a task started while another run of the same task was still in progress");

        var records = Records;
        records.Select(r => r.ExecutionId).Distinct().Count().ShouldBe(records.Count, "execution ids must be unique per run");

        foreach (var group in records.GroupBy(r => r.TaskId))
        {
            var ordered = group.OrderBy(r => r.Start).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                ordered[i].Start.ShouldBeGreaterThanOrEqualTo(ordered[i - 1].End,
                    $"task {group.Key}: run {ordered[i].ExecutionId} ({ordered[i].Host}) started before run {ordered[i - 1].ExecutionId} ({ordered[i - 1].Host}) ended");
        }
    }

    private sealed class Scope(RunLog log, string taskId, string executionId, string host, DateTimeOffset start) : IDisposable
    {
        public void Dispose() => log.End(taskId, executionId, host, start);
    }
}
