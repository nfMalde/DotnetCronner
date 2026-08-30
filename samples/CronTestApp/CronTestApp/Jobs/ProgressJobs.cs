using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// Progress reporting: <see cref="ICronnerJobContext"/> pulled straight from DI, total progress, and
/// per-subtask progress scopes. Every report raises a hook, which this app turns into
/// <c>GET /progress</c> and <c>GET /activity</c> entries.
/// </summary>
public sealed class ProgressJobs(ICronnerJobContext jobContext, JobActivityLog activity)
{
    /// <summary>A small summary the import job stashes in the run-state bag and onto the history record.</summary>
    public sealed record ImportSummary(int Scopes, string Note);

    /// <summary>
    /// What the <c>OnSuccess</c> hook turns the summary into: it reads the job's summary from the run-state bag
    /// and augments it (outcome + the execution id it is keyed by), recording it to the app's own log —
    /// application data belongs in your store, correlated by <c>ctx.ExecutionId</c>, not on the history row.
    /// </summary>
    public sealed record ImportSummaryWithOutcome(int Scopes, string Note, string Outcome, string ExecutionId);

    /// <summary>
    /// A two-phase import: total progress moves 0 → 1 while two scopes ("download", "index") report their
    /// own progress independently. Uses both the fire-and-forget and the awaitable form.
    /// </summary>
    [CronnerTask(id: "progress:import", cronstring: "*/30 * * * * *")]
    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        activity.Record("progress:import", "started — reporting total progress and two scopes");
        // The second argument to every report is a custom payload (ctx.ProgressPayload on the hook side);
        // here a per-report "current step" string. It surfaces in GET /progress as `note`.
        await jobContext.ProgressAsync(0m, "starting");

        await using (var download = jobContext.OpenProgressScope("download"))
        {
            for (var chunk = 1; chunk <= 4; chunk++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                download.Progress(chunk / 4m, $"chunk {chunk}/4");   // fire-and-forget scope progress + payload
            }

            await jobContext.ProgressAsync(0.5m, "download complete"); // awaited total progress + payload
        }

        await using (var index = jobContext.OpenProgressScope("index"))
        {
            for (var batch = 1; batch <= 2; batch++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                await index.ProgressAsync(batch / 2m, $"batch {batch}/2"); // awaited scope progress + payload
            }
        }

        await jobContext.ProgressAsync(1m, "done");

        // Run-state bag: hand a summary to this run's hooks (read via ctx.Get in OnSuccess). The hook records an
        // augmented version to the app's own log — application data stays in your store, not on the history row.
        var summary = new ImportSummary(Scopes: 2, Note: "download+index");
        jobContext.Set(summary);

        activity.Record("progress:import", $"finished at total progress {jobContext.TotalProgress:P0}");
    }

    /// <summary>
    /// Progress reported from a <em>lambda</em> task, with the context arriving as
    /// <c>HasParam&lt;ICronnerJobContext&gt;()</c> instead of through the constructor.
    /// </summary>
    public async Task ReindexAsync(ICronnerJobContext context, int steps, CancellationToken cancellationToken)
    {
        activity.Record("progress:reindex", $"started — {steps} steps, context via HasParam<ICronnerJobContext>()");

        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            // The payload rides along to the OnTotalProgressChange hook as ctx.ProgressPayload — here a
            // free-text "current step", the kind of per-report detail a progress scope's category can't carry.
            await context.ProgressAsync((decimal)step / steps, $"reindexing step {step} of {steps}");
        }

        activity.Record("progress:reindex", "finished");
    }
}
