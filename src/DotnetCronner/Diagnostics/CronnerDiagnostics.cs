namespace DotnetCronner;

/// <summary>
/// The names DotnetCronner publishes its telemetry under, and the tag keys it attaches. Subscribe to them
/// with any OpenTelemetry setup — <c>AddMeter(CronnerDiagnostics.MeterName)</c> and
/// <c>AddSource(CronnerDiagnostics.ActivitySourceName)</c> — or let the
/// <c>DotnetCronner.OpenTelemetry</c> package do it for you.
/// </summary>
/// <remarks>
/// The scheduler emits on the <see cref="System.Diagnostics.Metrics.Meter"/> and
/// <see cref="System.Diagnostics.ActivitySource"/> from the base class library, so the core package takes no
/// dependency on OpenTelemetry — nothing is collected, and nothing costs anything, until something listens.
/// </remarks>
public static class CronnerDiagnostics
{
    /// <summary>The meter every DotnetCronner metric is published on.</summary>
    public const string MeterName = "DotnetCronner";

    /// <summary>The activity source every DotnetCronner span is published on.</summary>
    public const string ActivitySourceName = "DotnetCronner";

    /// <summary>The name of the span covering one execution attempt.</summary>
    public const string ExecuteActivityName = "cronner.execute";

    /// <summary>Tag keys attached to DotnetCronner metrics and spans.</summary>
    /// <remarks>
    /// Every tag is bounded: a task id comes from the registry, and the rest are small enumerations — so the
    /// metric cardinality is a function of how many tasks you registered, never of what they process.
    /// </remarks>
    public static class Tags
    {
        /// <summary>The task's stable id.</summary>
        public const string TaskId = "cronner.task.id";

        /// <summary>The per-run correlation id (spans only — unbounded, so never a metric tag).</summary>
        public const string ExecutionId = "cronner.execution.id";

        /// <summary>The 1-based attempt number (spans only).</summary>
        public const string Attempt = "cronner.attempt";

        /// <summary>Total attempts the effective retry policy allows (spans only).</summary>
        public const string MaxAttempts = "cronner.max_attempts";

        /// <summary>How the run ended: <c>success</c>, <c>failed</c>, <c>cancelled</c> or <c>lock_lost</c>.</summary>
        public const string Outcome = "cronner.outcome";

        /// <summary>Whether the task is <c>recurring</c> or a <c>oneoff</c>.</summary>
        public const string JobKind = "cronner.job.kind";

        /// <summary>The misfire policy applied to a missed occurrence.</summary>
        public const string MisfirePolicy = "cronner.misfire.policy";

        /// <summary>The scheduler instance that owns the run (spans only).</summary>
        public const string Owner = "cronner.owner";
    }

    /// <summary>The values <see cref="Tags.Outcome"/> can take.</summary>
    public static class Outcomes
    {
        /// <summary>The run completed without throwing.</summary>
        public const string Success = "success";

        /// <summary>The job body threw.</summary>
        public const string Failed = "failed";

        /// <summary>The run was cancelled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>The run was abandoned because its execution lock was lost or could no longer be confirmed.</summary>
        public const string LockLost = "lock_lost";
    }
}
