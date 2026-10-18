using DotnetCronner;

namespace OpenTelemetry.Metrics;

/// <summary>Registers DotnetCronner's metrics with an OpenTelemetry meter provider.</summary>
public static class CronnerMeterProviderBuilderExtensions
{
    /// <summary>
    /// Collects every DotnetCronner metric: executions started / completed / failed / cancelled, how long
    /// they took, how late they started, retries, misfires, lock claims and losses, and how many runs are in
    /// flight right now.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>AddMeter(CronnerDiagnostics.MeterName)</c> — the scheduler publishes on a plain
    /// base-class-library <see cref="System.Diagnostics.Metrics.Meter"/>, so this package exists for
    /// discoverability, not because the core needs it. Every metric is tagged with the task id, which is
    /// bounded by what you registered; nothing a job processes ever becomes a tag.
    /// </remarks>
    public static MeterProviderBuilder AddCronnerInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(CronnerDiagnostics.MeterName);
    }
}
