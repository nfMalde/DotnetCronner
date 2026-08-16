using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Jobs;

/// <summary>
/// The plain <c>[CronnerTask]</c> surface: cron variants, priorities, DI-resolved parameters and a
/// manual (cron-less) task. All of these are found by assembly scanning — nothing in Program.cs
/// registers them.
/// </summary>
public sealed class AttributeJobs(IGreeter greeter, JobActivityLog activity, ILogger<AttributeJobs> logger)
{
    /// <summary>Classic 5-field cron: every minute.</summary>
    [CronnerTask(id: "attr:heartbeat", cronstring: "* * * * *")]
    public Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        activity.Record("attr:heartbeat", "5-field cron '* * * * *' (every minute)");
        return Task.CompletedTask;
    }

    /// <summary>6-field cron with a leading seconds field, at the lowest priority.</summary>
    [CronnerTask(id: "attr:every-5-seconds", cronstring: "*/5 * * * * *", Priority = CronnerTaskPriority.Low)]
    public void EveryFiveSeconds() =>
        activity.Record("attr:every-5-seconds", "6-field cron '*/5 * * * * *' (seconds field), Priority=Low");

    /// <summary>
    /// Highest priority, and its parameters come from DI rather than the constructor — both are resolved
    /// from the execution scope.
    /// </summary>
    [CronnerTask(id: "attr:critical", cronstring: "*/15 * * * * *", Priority = CronnerTaskPriority.Critical)]
    public Task CriticalAsync(IGreeter injectedGreeter, ScopeMarker scope, CancellationToken cancellationToken)
    {
        injectedGreeter.Greet("critical task", "attr:critical");
        activity.Record("attr:critical", $"Priority=Critical, method parameters resolved from DI (scope {scope.Id})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// No cron string, so this one never fires on its own — trigger it with
    /// <c>POST /tasks/attr:manual/run</c>.
    /// </summary>
    [CronnerTask(id: "attr:manual")]
    public Task ManualAsync(CancellationToken cancellationToken)
    {
        greeter.Greet("manual trigger", "attr:manual");
        activity.Record("attr:manual", "manual/one-shot task (no cron) triggered via ICronnerClient.ScheduleTaskAsync");
        return Task.CompletedTask;
    }

    /// <summary>Cron syntax workout: lists, ranges, steps and day names, evaluated in <c>CRONNER_TIMEZONE</c>.</summary>
    [CronnerTask(id: "attr:business-hours", cronstring: "0,30 8-18/2 * * MON-FRI")]
    public void BusinessHours()
    {
        logger.LogInformation("business-hours task fired");
        activity.Record("attr:business-hours", "lists + ranges + steps + day names ('0,30 8-18/2 * * MON-FRI')");
    }

    /// <summary>
    /// Daily at 03:30 <em>in the configured time zone</em> — check <c>nextRunUtc</c> on <c>GET /tasks</c>
    /// after changing <c>CRONNER_TIMEZONE</c>.
    /// </summary>
    [CronnerTask(id: "attr:daily-0330", cronstring: "30 3 * * ?")]
    public void DailyInTimeZone() =>
        activity.Record("attr:daily-0330", "daily 03:30 in the configured time zone ('?' alias for '*')");

    /// <summary>Auto-generated id: no <c>id</c> argument, so the id is derived from Type.Method.</summary>
    [CronnerTask(cronstring: "*/30 * * * * *")]
    public void AutoNamedTask() =>
        activity.Record(
            "CronTestApp.Jobs.AttributeJobs.AutoNamedTask",
            "no explicit id — derived from the fully qualified Type.Method name");
}

/// <summary>Static <c>[CronnerTask]</c> methods are discovered too; there is no instance, so every
/// dependency arrives as a parameter.</summary>
public static class StaticJobs
{
    /// <summary>A static task whose parameters are resolved from the execution scope.</summary>
    [CronnerTask(id: "attr:static", cronstring: "*/45 * * * * *")]
    public static void Tick(JobActivityLog activity, ILogger<AttributeJobs> logger)
    {
        logger.LogInformation("static task ran");
        activity.Record("attr:static", "static method — no instance, all parameters from DI");
    }
}
