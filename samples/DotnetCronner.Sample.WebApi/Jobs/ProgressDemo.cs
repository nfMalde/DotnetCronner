using System.Collections.Concurrent;
using DotnetCronner;

namespace DotnetCronner.Sample.WebApi.Jobs;

/// <summary>
/// A job that reports both total and scope progress, each report carrying a custom payload (here a
/// per-report "current step" string). Scheduled in <c>Program.cs</c>; observed via <c>GET /progress</c>.
/// It also stores a small summary on its execution-history row, keyed by the run's
/// <see cref="ICronnerJobContext.ExecutionId"/> — the id every hook of the run sees too.
/// </summary>
public sealed class ImportJob
{
    /// <summary>What the job records about itself; the hook augments it afterwards (see <see cref="ProgressHook"/>).</summary>
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

        // Persist a summary onto this run's history row (its Data slot). ctx.ExecutionId is the row's Id, so a
        // per-run artefact of your own (a log file, a display label) can be keyed to it without a second table.
        ctx.SetExecutionData(new ImportSummary(Chunks: 3, ExecutionId: ctx.ExecutionId));
    }
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

/// <summary>Feeds <see cref="ProgressLog"/> from the total and scope progress hooks, reading the per-report payload.</summary>
public sealed class ProgressHook(ProgressLog log) : ICronnerTaskHook
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
    /// The execution-data slot is readable as well as writable: read back what the job stored for THIS run
    /// (same <c>context.ExecutionId</c>) and augment it, instead of keeping a second record. The result lands on
    /// the history row's <c>Data</c> — see <c>GET /tasks/import/history</c>.
    /// </remarks>
    public Task OnSuccessAsync(CronnerTaskContext context)
    {
        if (context.TryGetExecutionData<ImportJob.ImportSummary>(out var summary))
            context.SetExecutionData(summary with { Outcome = $"succeeded in {context.Duration.TotalMilliseconds:0} ms" });
        return Task.CompletedTask;
    }
}
