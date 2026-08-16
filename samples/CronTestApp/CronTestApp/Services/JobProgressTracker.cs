using System.Collections.Concurrent;
using DotnetCronner;

namespace CronTestApp.Services;

/// <summary>
/// Rebuilds each task's progress purely from the progress hooks — nothing else feeds it. Served from
/// <c>GET /progress</c>, so if the numbers look right the hooks fired with the right payloads.
/// </summary>
public sealed class JobProgressTracker
{
    private readonly ConcurrentDictionary<string, JobProgress> _byJob = new();

    /// <summary>Records total progress (from <c>OnTotalProgressChange</c>).</summary>
    public void Total(string jobId, decimal value)
    {
        var progress = _byJob.GetOrAdd(jobId, static _ => new JobProgress());
        progress.Total = value;
        progress.LastUpdateUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Records a scope opening (from <c>OnProgressScopeOpened</c>).</summary>
    public void ScopeOpened(string jobId, CronnerProgressInfo scope) => UpdateScope(jobId, scope, open: true);

    /// <summary>Records progress inside a scope (from <c>OnScopeProgress</c>).</summary>
    public void ScopeProgress(string jobId, CronnerProgressInfo scope) => UpdateScope(jobId, scope, open: true);

    /// <summary>Records a scope closing (from <c>OnProgressScopeClosed</c>).</summary>
    public void ScopeClosed(string jobId, CronnerProgressInfo scope) => UpdateScope(jobId, scope, open: false);

    /// <summary>A JSON-friendly snapshot of every task that has reported progress.</summary>
    public object Snapshot() => _byJob
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(pair => pair.Key, pair => (object)new
        {
            total = pair.Value.Total,
            lastUpdateUtc = pair.Value.LastUpdateUtc,
            scopes = pair.Value.Scopes.Values
                .OrderBy(scope => scope.Category, StringComparer.Ordinal)
                .Select(scope => new { scope.Id, scope.Category, scope.Value, scope.Open, scope.Reports })
                .ToArray(),
        });

    private void UpdateScope(string jobId, CronnerProgressInfo scope, bool open)
    {
        var progress = _byJob.GetOrAdd(jobId, static _ => new JobProgress());
        var state = progress.Scopes.GetOrAdd(scope.Id, id => new ScopeState { Id = id, Category = scope.Category });
        state.Value = scope.Value;
        state.Open = open;
        state.Reports++;
        progress.LastUpdateUtc = DateTimeOffset.UtcNow;
    }

    private sealed class JobProgress
    {
        public decimal Total { get; set; }

        public DateTimeOffset LastUpdateUtc { get; set; }

        public ConcurrentDictionary<string, ScopeState> Scopes { get; } = new();
    }

    private sealed class ScopeState
    {
        public required string Id { get; init; }

        public string? Category { get; init; }

        public decimal Value { get; set; }

        public bool Open { get; set; }

        public int Reports { get; set; }
    }
}
