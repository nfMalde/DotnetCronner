using System.Collections.Concurrent;
using DotnetCronner;

namespace DotnetCronner.Sample.WebApi.Jobs;

/// <summary>
/// A job that reports both total and scope progress, each report carrying a custom payload (here a
/// per-report "current step" string). Scheduled in <c>Program.cs</c>; observed via <c>GET /progress</c>.
/// It also keeps a small application summary about the run in the app's <em>own</em> store
/// (<see cref="RunSummaryStore"/>), keyed by the run's <see cref="ICronnerJobContext.ExecutionId"/> — the id
/// every hook of the run sees too, so the hook can augment the same record.
/// </summary>
public sealed class ImportJob(RunSummaryStore summaries)
{
    /// <summary>What the app records about a run; the hook augments it afterwards (see <see cref="ProgressHook"/>).</summary>
    public sealed record ImportSummary(int Chunks, string ExecutionId, string? Outcome = null);

    /// <summary>The context arrives via <c>HasParam&lt;ICronnerJobContext&gt;()</c> from the schedule lambda.</summary>
    public async Task RunAsync(ICronnerJobContext ctx, CancellationToken cancellationToken)
    {
        await ctx.ProgressAsync(0m, "starting");                       // total + payload

        await using (var download = ctx.OpenProgressScope("download"))
        {
            for (var chunk = 1; chunk <= 3; chunk++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                download.Progress(chunk / 3m, $"chunk {chunk}/3");    // scope + payload
            }

            await ctx.ProgressAsync(0.6m, "download complete");        // total + payload
        }

        await using (var index = ctx.OpenProgressScope("index"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            await index.ProgressAsync(1m, "indexed");                  // scope + payload
        }

        await ctx.ProgressAsync(1m, "done");

        // The recommended pattern: keep application data in your OWN store, correlated by the execution id.
        // ctx.ExecutionId matches the history row's id and is visible to every hook of this run, so the hook
        // can find and augment the same summary (see ProgressHook.OnSuccessAsync). Exposed at GET /runs.
        summaries.Save(new ImportSummary(Chunks: 3, ExecutionId: ctx.ExecutionId));
    }
}

/// <summary>
/// The application's own per-run summary store, keyed by execution id. This is what replaces the deprecated
/// execution-data slot: execution history records <em>an execution</em>; your own data lives here, correlated
/// by <see cref="ICronnerJobContext.ExecutionId"/>. Served from <c>GET /runs</c>.
/// </summary>
public sealed class RunSummaryStore
{
    private readonly ConcurrentDictionary<string, ImportJob.ImportSummary> _byExecution = new(StringComparer.Ordinal);

    /// <summary>Saves (or replaces) the summary for its execution id.</summary>
    public void Save(ImportJob.ImportSummary summary) => _byExecution[summary.ExecutionId] = summary;

    /// <summary>Reads back the summary for an execution id, if any.</summary>
    public bool TryGet(string executionId, out ImportJob.ImportSummary summary) =>
        _byExecution.TryGetValue(executionId, out summary!);

    /// <summary>A JSON-friendly snapshot, newest arbitrary order.</summary>
    public IReadOnlyCollection<ImportJob.ImportSummary> Snapshot() => _byExecution.Values.ToArray();
}

/// <summary>Latest progress per task, rebuilt purely from the progress hooks. Served from <c>GET /progress</c>.</summary>
public sealed class ProgressLog
{
    private readonly ConcurrentDictionary<string, Entry> _byJob = new(StringComparer.Ordinal);

    /// <summary>Records total progress and its payload note (from <c>OnTotalProgressChange</c>).</summary>
    public void Total(string jobId, decimal total, string? note)
    {
        var entry = _byJob.GetOrAdd(jobId, static _ => new Entry());
        entry.Total = total;
        if (note is not null)
            entry.Note = note;
    }

    /// <summary>Records the latest scope report and its payload note (from <c>OnScopeProgress</c>).</summary>
    public void Scope(string jobId, string? category, decimal value, string? note)
    {
        var entry = _byJob.GetOrAdd(jobId, static _ => new Entry());
        entry.LastScope = $"{category} {value:P0}" + (note is not null ? $" ({note})" : "");
    }

    /// <summary>A JSON-friendly snapshot of every task that has reported progress.</summary>
    public object Snapshot() => _byJob
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(pair => pair.Key, pair => (object)new { total = pair.Value.Total, note = pair.Value.Note, lastScope = pair.Value.LastScope });

    private sealed class Entry
    {
        public decimal Total;
        public string? Note;
        public string? LastScope;
    }
}

/// <summary>Feeds <see cref="ProgressLog"/> from the progress hooks, and augments the run summary on success.</summary>
public sealed class ProgressHook(ProgressLog log, RunSummaryStore summaries) : ICronnerTaskHook
{
    /// <inheritdoc />
    public Task OnTotalProgressChangeAsync(CronnerTaskContext context)
    {
        log.Total(context.Job.Id, context.TotalProgress, context.ProgressPayload as string);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnScopeProgressAsync(CronnerTaskContext context)
    {
        log.Scope(context.Job.Id, context.ProgressScope!.Category, context.ProgressScope.Value, context.ProgressPayload as string);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The hook augments the app's own summary for THIS run — found by <c>context.ExecutionId</c>, the same id
    /// the job used — instead of keeping a second copy. It records the duration from the history row via
    /// <c>context.Duration</c>. See <c>GET /runs</c>.
    /// </remarks>
    public Task OnSuccessAsync(CronnerTaskContext context)
    {
        if (summaries.TryGet(context.ExecutionId, out var summary))
            summaries.Save(summary with { Outcome = $"succeeded in {context.Duration.TotalMilliseconds:0} ms" });
        return Task.CompletedTask;
    }
}
