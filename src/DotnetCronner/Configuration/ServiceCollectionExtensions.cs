using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DotnetCronner;

/// <summary>Registration entry points for DotnetCronner.</summary>
public static class CronnerServiceCollectionExtensions
{
    /// <summary>
    /// Registers DotnetCronner and its background scheduler. Optionally configure stores, caching and
    /// tasks here; the same configuration surface is also available via <c>app.UseDotnetCronner(...)</c>.
    /// </summary>
    public static IServiceCollection AddDotnetCronner(this IServiceCollection services, Action<ICronnerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CronnerOptions();
        var holder = new CronnerStoreHolder();
        var registry = new CronnerRegistry();
        var hooks = new CronnerHookRegistry();
        var jobServices = new CronnerJobServices();

        services.TryAddSingleton(options);
        services.TryAddSingleton(holder);
        services.TryAddSingleton(registry);
        services.TryAddSingleton(hooks);
        services.TryAddSingleton<CronnerHookDispatcher>();
        services.TryAddSingleton(jobServices);
        services.TryAddScoped<CronnerJobContext>();
        services.TryAddScoped<ICronnerJobContext>(sp => sp.GetRequiredService<CronnerJobContext>());
        services.TryAddSingleton<CronnerScheduleSignal>();
        services.TryAddSingleton<CronnerExecutionTracker>();
        services.TryAddSingleton<CronnerScheduleCalculator>();
        // Observability. Nothing is collected until an OpenTelemetry (or other) listener subscribes, and the
        // core package takes no dependency to provide it: IMeterFactory is used when the host already has one
        // (ASP.NET Core does), and otherwise the instrumentation owns its meter.
        services.TryAddSingleton<CronnerSchedulerDiagnostics>();
        services.TryAddSingleton(sp => new CronnerInstrumentation(
            sp.GetService<IMeterFactory>(), sp.GetRequiredService<CronnerSchedulerDiagnostics>()));
        services.TryAddSingleton<IOptions<CronnerOptions>>(sp => Options.Create(sp.GetRequiredService<CronnerOptions>()));
        // Scoped, not singleton: this follows the ambient scope, so a store configured as
        // CronnerStoreLifetime.Scoped is built per scope with that scope's dependencies. A Singleton
        // store still resolves to the one cached instance, so nothing changes for in-memory, Redis or
        // EF. The engine does not inject this directly -- it opens a scope per operation.
        services.TryAddScoped<ICronnerStore>(sp => sp.GetRequiredService<CronnerStoreHolder>().Resolve(sp));
        services.TryAddSingleton<CronnerStoreAccessor>();
        services.TryAddSingleton<ICronnerClient, CronnerClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CronnerHostedService>());

        if (configure is not null)
            configure(new CronnerBuilder(services, options, holder, registry, hooks, jobServices));

        return services;
    }
}
