using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>A typed payload for a one-off enqueued job (a plain, serializable DTO — never an ORM proxy).</summary>
public sealed record NotificationPayload(string To, string Message, int Attempt = 1);

/// <summary>
/// 0.0.3: an <b>enqueue-only</b> task — a <c>[CronnerTask]</c> with no cron. It never runs on its own; you
/// enqueue a single instance with a typed payload via <c>ICronnerClient.EnqueueAsync</c> (see the
/// <c>/enqueue/notify</c> endpoint). The payload arrives as an ordinary method parameter; everything else
/// (here <see cref="ICronnerJobContext"/>) still resolves from DI.
/// </summary>
public sealed class EnqueueJobs
{
    private readonly JobActivityLog _activity;

    public EnqueueJobs(JobActivityLog activity) => _activity = activity;

    [CronnerTask("enqueue:notify", Description = "One-off: send a notification carrying a typed payload")]
    public Task SendAsync(NotificationPayload payload, ICronnerJobContext ctx, CancellationToken ct)
    {
        _activity.Record(
            "enqueue:notify",
            $"one-off delivered payload → to='{payload.To}', message=\"{payload.Message}\", attempt={payload.Attempt}");
        return Task.CompletedTask;
    }
}
