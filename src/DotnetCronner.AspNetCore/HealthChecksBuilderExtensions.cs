using DotnetCronner;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the DotnetCronner scheduler health check to an application's health checks.</summary>
public static class CronnerHealthChecksBuilderExtensions
{
    /// <summary>The name the check is registered under when none is given.</summary>
    public const string DefaultName = "dotnetcronner";

    /// <summary>
    /// Reports whether the scheduler started, is still polling for due work, and can reach its store.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="configure">Adjusts what the check looks at — see <see cref="CronnerHealthCheckOptions"/>.</param>
    /// <param name="name">The registration name. Defaults to <see cref="DefaultName"/>.</param>
    /// <param name="failureStatus">
    /// The status reported when the scheduler is broken. Defaults to <see cref="HealthStatus.Unhealthy"/>;
    /// pass <see cref="HealthStatus.Degraded"/> when the scheduler is not critical to the application and
    /// its being down should not take the process out of rotation.
    /// </param>
    /// <param name="tags">Tags for filtering (for example a <c>"ready"</c> endpoint).</param>
    /// <remarks>
    /// Call it after <c>AddDotnetCronner(...)</c> — the check reads the same scheduler diagnostics and store
    /// registration. A job that throws never makes this check fail; see <see cref="CronnerHealthCheckOptions"/>.
    /// </remarks>
    public static IHealthChecksBuilder AddCronner(
        this IHealthChecksBuilder builder,
        Action<CronnerHealthCheckOptions>? configure = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new CronnerHealthCheckOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        return builder.AddCheck<CronnerHealthCheck>(
            name ?? DefaultName,
            failureStatus ?? HealthStatus.Unhealthy,
            tags ?? []);
    }
}
