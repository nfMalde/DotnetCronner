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
    /// <summary>
    /// A two-phase import: total progress moves 0 → 1 while two scopes ("download", "index") report their
    /// own progress independently. Uses both the fire-and-forget and the awaitable form.
    /// </summary>
    [CronnerTask(id: "progress:import", cronstring: "*/30 * * * * *")]
    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        activity.Record("progress:import", "started — reporting total progress and two scopes");
        await jobContext.ProgressAsync(0m);

        await using (var download = jobContext.OpenProgressScope("download"))
        {
            for (var chunk = 1; chunk <= 4; chunk++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                download.Progress(chunk / 4m);          // fire-and-forget scope progress
            }

            await jobContext.ProgressAsync(0.5m);       // awaited total progress
        }

        await using (var index = jobContext.OpenProgressScope("index"))
        {
            for (var batch = 1; batch <= 2; batch++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                await index.ProgressAsync(batch / 2m);  // awaited scope progress
            }
        }

        await jobContext.ProgressAsync(1m);
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
            await context.ProgressAsync((decimal)step / steps);
        }

        activity.Record("progress:reindex", "finished");
    }
}
