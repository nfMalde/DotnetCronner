using DotnetCronner;

namespace OpenTelemetry.Trace;

/// <summary>Registers DotnetCronner's spans with an OpenTelemetry tracer provider.</summary>
public static class CronnerTracerProviderBuilderExtensions
{
    /// <summary>
    /// Collects one span per execution attempt, tagged with the task id, execution id and attempt number.
    /// The span is the ambient activity while the job body runs, so the spans the job produces itself —
    /// EF Core queries, outbound HTTP — appear underneath it rather than as orphans.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>AddSource(CronnerDiagnostics.ActivitySourceName)</c>. A retry is a separate
    /// execution and therefore a separate span, carrying its own <c>cronner.attempt</c>.
    /// </remarks>
    public static TracerProviderBuilder AddCronnerInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(CronnerDiagnostics.ActivitySourceName);
    }
}
