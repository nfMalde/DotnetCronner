using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// The three concurrency policies, all firing every 5 seconds while taking longer than that to run —
/// so each occurrence overlaps the previous one and the policy becomes visible in <c>GET /activity</c>:
/// <list type="bullet">
///   <item><description><c>conc:drop</c> — ticks that arrive while a run is in flight are discarded.</description></item>
///   <item><description><c>conc:queue</c> — the missed tick runs right after the current one, never overlapping.</description></item>
///   <item><description><c>conc:parallel</c> — runs pile up side by side (watch <c>inFlight</c> climb).</description></item>
/// </list>
/// </summary>
public sealed class ConcurrencyJobs(JobActivityLog activity)
{
    private static int _dropRuns;
    private static int _queueRuns;
    private static int _parallelRuns;
    private static int _parallelInFlight;

    /// <summary>Default policy: a tick during an in-flight run is dropped.</summary>
    [CronnerTask(id: "conc:drop", cronstring: "*/5 * * * * *", Concurrency = CronnerConcurrencyMode.DropAndForget)]
    public Task DropAndForgetAsync(CancellationToken cancellationToken) =>
        RunSlowlyAsync("conc:drop", Interlocked.Increment(ref _dropRuns), TimeSpan.FromSeconds(12), cancellationToken);

    /// <summary>Queued policy: the missed occurrence runs immediately after this one finishes.</summary>
    [CronnerTask(id: "conc:queue", cronstring: "*/5 * * * * *", Concurrency = CronnerConcurrencyMode.Queue)]
    public Task QueuedAsync(CancellationToken cancellationToken) =>
        RunSlowlyAsync("conc:queue", Interlocked.Increment(ref _queueRuns), TimeSpan.FromSeconds(8), cancellationToken);

    /// <summary>Concurrent policy: overlapping runs are allowed.</summary>
    [CronnerTask(id: "conc:parallel", cronstring: "*/5 * * * * *", Concurrency = CronnerConcurrencyMode.Concurrent)]
    public async Task ConcurrentAsync(CancellationToken cancellationToken)
    {
        var run = Interlocked.Increment(ref _parallelRuns);
        var inFlight = Interlocked.Increment(ref _parallelInFlight);
        try
        {
            activity.Record("conc:parallel", $"run #{run} started (inFlight={inFlight})");
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            activity.Record("conc:parallel", $"run #{run} finished");
        }
        finally
        {
            Interlocked.Decrement(ref _parallelInFlight);
        }
    }

    private async Task RunSlowlyAsync(string jobId, int run, TimeSpan duration, CancellationToken cancellationToken)
    {
        activity.Record(jobId, $"run #{run} started, will take {duration.TotalSeconds:0}s");
        await Task.Delay(duration, cancellationToken);
        activity.Record(jobId, $"run #{run} finished");
    }
}
