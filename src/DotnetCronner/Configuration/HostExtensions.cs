using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetCronner;

/// <summary>Post-build configuration entry point for DotnetCronner.</summary>
public static class CronnerHostExtensions
{
    /// <summary>
    /// Configures DotnetCronner after the host has been built — the natural place to declare stores,
    /// caching and scheduled tasks, e.g.
    /// <code>
    /// app.UseDotnetCronner(cronner =&gt; cronner
    ///     .UseStore&lt;MyStore&gt;()
    ///     .Sched&lt;MyJobs&gt;(x =&gt; x.Run(x.HasParam&lt;CancellationToken&gt;()), o =&gt; o.WithCron("*/5 * * * *")));
    /// </code>
    /// Requires <see cref="CronnerServiceCollectionExtensions.AddDotnetCronner"/> to have been called on the
    /// service collection first. Custom store implementations are resolved from the built container, so any
    /// dependencies they need must be registered before the host is built.
    /// </summary>
    public static IHost UseDotnetCronner(this IHost host, Action<ICronnerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(configure);

        var options = host.Services.GetService<CronnerOptions>()
            ?? throw new InvalidOperationException(
                "DotnetCronner is not registered. Call services.AddDotnetCronner(...) before app.UseDotnetCronner(...).");
        var holder = host.Services.GetRequiredService<CronnerStoreHolder>();
        var registry = host.Services.GetRequiredService<CronnerRegistry>();
        var hooks = host.Services.GetRequiredService<CronnerHookRegistry>();
        var jobServices = host.Services.GetRequiredService<CronnerJobServices>();

        // A detached service collection: post-build service registrations cannot take effect, so store,
        // cache, hook and job-service selections flow through the singletons resolved above instead.
        configure(new CronnerBuilder(new ServiceCollection(), options, holder, registry, hooks, jobServices));
        return host;
    }
}
