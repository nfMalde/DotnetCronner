using CronTestApp.Data;
using CronTestApp.ExternalJobs;
using CronTestApp.Hooks;
using CronTestApp.Jobs;
using CronTestApp.Services;
using CronTestApp.Stores;
using DotnetCronner;
using Microsoft.EntityFrameworkCore;

namespace CronTestApp.Configuration;

/// <summary>
/// All DotnetCronner wiring for this test app, driven entirely by <see cref="TestAppOptions"/> (that is,
/// by the <c>.env</c> file). Registration-time concerns — store, cache, discovery, DI hooks — happen in
/// <see cref="AddTestAppCronner"/>; scheduling and the fluent hooks happen in
/// <see cref="UseTestAppCronner"/>, mirroring how the README splits the two.
/// </summary>
public static class CronnerSetup
{
    /// <summary>Registers the app's own services plus DotnetCronner with the configured store and cache.</summary>
    public static IServiceCollection AddTestAppCronner(this IServiceCollection services, TestAppOptions options)
    {
        // Shared singletons. The dedicated job container gets these exact instances, so /activity,
        // /metrics and /progress show the same data in both CRONNER_JOB_SERVICES modes.
        var activity = new JobActivityLog();
        var metrics = new JobMetrics();
        var progress = new JobProgressTracker();
        var cadence = new LockCadenceTracker();

        services.AddSingleton(options);
        services.AddSingleton(activity);
        services.AddSingleton<IJobActivitySink>(activity);
        services.AddSingleton(metrics);
        services.AddSingleton(progress);
        services.AddSingleton(cadence);
        AddJobDependencies(services);

        services.AddDotnetCronner(cronner =>
        {
            cronner.Configure(cronnerOptions =>
            {
                cronnerOptions.TimeZone = options.TimeZone;
                cronnerOptions.PollingInterval = options.PollingInterval;
                cronnerOptions.MaxConcurrentTasks = options.MaxConcurrentTasks;
                cronnerOptions.LockTtl = options.LockTtl;
                cronnerOptions.DefaultMaxRetries = options.MaxRetries;
                cronnerOptions.RetryDelay = options.RetryDelay;
            });

            ConfigureStore(cronner, options);
            ConfigureCache(cronner, options);
            ConfigureDiscovery(cronner, options);

            if (options.JobServices is JobServicesMode.Dedicated)
            {
                // Jobs (and the hooks resolved per run) come from an isolated container instead of the
                // web app's provider. Everything a job touches has to be registered here.
                cronner.WithDedicatedDI(dedicated =>
                {
                    dedicated.AddLogging(logging => logging.AddSimpleConsole(console => console.TimestampFormat = "HH:mm:ss "));
                    dedicated.AddSingleton(options);
                    dedicated.AddSingleton(activity);
                    dedicated.AddSingleton<IJobActivitySink>(activity);
                    dedicated.AddSingleton(metrics);
                    dedicated.AddSingleton(progress);
                    dedicated.AddSingleton(cadence);
                    AddJobDependencies(dedicated);
                    dedicated.AddScoped<ICronnerTaskHook, LoggingHook>();
                }, jobLifetime: ServiceLifetime.Scoped);
            }
            else
            {
                // A hook type resolved from DI once per execution scope.
                cronner.AddHook<LoggingHook>();
            }
        });

        return services;
    }

    /// <summary>Schedules the lambda tasks and registers the fluent lifecycle hooks.</summary>
    public static IHost UseTestAppCronner(this IHost host, TestAppOptions options)
    {
        var activity = host.Services.GetRequiredService<JobActivityLog>();
        var cadence = host.Services.GetRequiredService<LockCadenceTracker>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cronner.Hooks");

        return host.UseDotnetCronner(cronner => cronner
            // ── Cadence probe: a new run resets the baseline, then each keepalive is timestamped. ──
            // Registered FIRST so a slower hook behind it can never skew the measurement.
            .OnLockAcquire(context =>
            {
                cadence.StartRun(context.Job.Id);
                return Task.CompletedTask;
            })
            .OnKeepAlive(context =>
            {
                cadence.Record(context.Job.Id);
                return Task.CompletedTask;
            })

            // Optional: a deliberately slow keepalive hook (CRONNER_SLOW_KEEPALIVE_MS). Set it longer than
            // LockTtl/2 and check GET /locks — the renewal cadence must stay put, because the scheduler
            // fires keepalive hooks detached from the renewal loop.
            .OnKeepAlive(async context =>
            {
                if (options.SlowKeepAliveDelay <= TimeSpan.Zero)
                    return;

                await Task.Delay(options.SlowKeepAliveDelay, context.CancellationToken);
                activity.Record(
                    context.Job.Id,
                    $"[lock] slow keepalive hook finished after {options.SlowKeepAliveDelay.TotalSeconds:0.#}s (ran detached)");
            })
            // ── Fluent (delegate) hooks: one registration per lifecycle event. ─────────────────────
            .OnStart(context =>
            {
                logger.LogDebug("[delegate hook] {TaskId} starting", context.Job.Id);
                return Task.CompletedTask;
            })
            .OnSuccess(context =>
            {
                activity.Record(context.Job.Id, $"[delegate hook] succeeded in {context.Duration.TotalMilliseconds:0} ms");
                return Task.CompletedTask;
            })
            .OnFail(context =>
            {
                logger.LogWarning("[delegate hook] {TaskId} failed: {Error}", context.Job.Id, context.Exception?.Message);
                return Task.CompletedTask;
            })
            .OnCancel(context =>
            {
                logger.LogWarning("[delegate hook] {TaskId} cancelled", context.Job.Id);
                return Task.CompletedTask;
            })

            // ── Lock hooks: the alerting case for a claim that disappeared mid-run. ────────────────
            // (OnLockAcquire / OnKeepAlive / OnLockRelease are covered for every task by LoggingHook.)
            .OnLockLost(context =>
            {
                logger.LogError(
                    "[delegate hook] {TaskId} LOST its lock after {Duration} — another worker owns it now",
                    context.Job.Id, context.Duration);
                return Task.CompletedTask;
            })

            // ── Lambda tasks: literals, DI parameters, the factory overload, async, and manual. ────
            .Sched<LambdaJobs>(
                x => x.SayHello("world", 2, x.HasParam<CancellationToken>()),
                schedule => schedule
                    .WithCron("*/10 * * * * *")
                    .WithId("lambda:hello")
                    .WithPrio(CronnerTaskPriority.High)
                    .WithConcurrency(CronnerConcurrencyMode.DropAndForget))

            .Sched<LambdaJobs>(
                x => x.UseService(x.HasParam<IGreeter>(), x.HasParam<ScopeMarker>(), x.HasParam<CancellationToken>()),
                schedule => schedule.WithCron("*/25 * * * * *").WithId("lambda:service"))

            .Sched<LambdaJobs>(
                x => x.ForTenant(x.HasParam<Tenant>(sp => sp.GetRequiredService<ITenantAccessor>().Current)),
                schedule => schedule.WithCron("*/35 * * * * *").WithId("lambda:tenant"))

            // An async target: binds to the Expression<Func<TJob, Task>> overload, so there is no CS4014
            // at the call site, and the scheduler awaits the returned Task before the run counts as done.
            .Sched<LambdaJobs>(
                x => x.ImportAsync(250, x.HasParam<CancellationToken>()),
                schedule => schedule
                    .WithCron("*/15 * * * * *")
                    .WithId("lambda:import")
                    .WithConcurrency(CronnerConcurrencyMode.Queue))

            // ── Per-schedule hooks: every hook style, attached to this one task and no other. ──────
            .Sched<ProgressJobs>(
                x => x.ReindexAsync(x.HasParam<ICronnerJobContext>(), 5, x.HasParam<CancellationToken>()),
                schedule => schedule
                    .WithCron("*/45 * * * * *")
                    .WithId("progress:reindex")
                    // a) a hook class, scoped to this schedule
                    .WithHook<ScheduleScopedHook>()
                    // b) a synchronous method-call expression, arguments resolved with HasParam
                    .OnStart<AuditHook>(h => h.Record(h.HasParam<JobActivityLog>(), "progress:reindex starting"))
                    // c) an async method-call expression
                    .OnSuccess<AuditHook>(h => h.RecordAsync(
                        h.HasParam<JobActivityLog>(), h.HasParam<ScopeMarker>(), "progress:reindex finished"))
                    // d) a plain delegate, here on a progress event
                    .OnTotalProgressChange(context =>
                    {
                        activity.Record(context.Job.Id, $"[per-schedule hook] total progress {context.TotalProgress:P0}");
                        return Task.CompletedTask;
                    }))

            // The short overload: expression + cron string, id derived from Type.Method.
            .Sched<LambdaJobs>(x => x.SayHello("short overload", 1, x.HasParam<CancellationToken>()), "0 * * * *")

            // No cron string at all → manual only, run it with POST /tasks/lambda:manual/run.
            .Sched<LambdaJobs>(x => x.ManualOnly(), schedule => schedule.WithId("lambda:manual")));
    }

    /// <summary>Services the job classes depend on, registered into whichever container runs the jobs.</summary>
    private static void AddJobDependencies(IServiceCollection services)
    {
        services.AddScoped<IGreeter, ConsoleGreeter>();
        services.AddScoped<ScopeMarker>();
        services.AddScoped<ITenantAccessor, RoundRobinTenantAccessor>();

        // Hook targets: the expression hooks resolve AuditHook, and WithHook<T>() resolves this one —
        // both from the hook invocation's own scope, so they live in whichever container runs the jobs.
        services.AddScoped<AuditHook>();
        services.AddScoped<ScheduleScopedHook>();
    }

    private static void ConfigureStore(ICronnerBuilder cronner, TestAppOptions options)
    {
        switch (options.Store)
        {
            case StoreKind.Memory:
                // Nothing to configure — the in-memory store is the default.
                break;

            case StoreKind.Redis:
                cronner.UseRedisAsStore(redis =>
                {
                    redis.Configuration = options.RedisConnection;
                    redis.KeyPrefix = options.RedisKeyPrefix;
                });
                break;

            case StoreKind.EfPostgres:
                // The package's ready-made context: it registers the context factory itself.
                cronner.UseEntityFrameworkStore(db => db.UseNpgsql(options.PostgresConnection));
                break;

            case StoreKind.EfPostgresAppContext:
                // This app's own DbContext, which implements ICronnerDbContext and calls ApplyCronnerModel().
                cronner.Services.AddDbContextFactory<AppDbContext>(db => db.UseNpgsql(options.PostgresConnection));
                cronner.UseEntityFrameworkStore<AppDbContext>();
                break;

            case StoreKind.Custom:
                cronner.UseStore<JsonFileCronnerStore>();
                break;

            default:
                throw new InvalidOperationException($"Unhandled store '{options.Store}'.");
        }
    }

    private static void ConfigureCache(ICronnerBuilder cronner, TestAppOptions options)
    {
        if (options.Cache is not CacheKind.Redis)
            return;

        cronner.UseSecondLevelCache(cache => cache.UseRedisCacheProvider(redis =>
        {
            redis.Configuration = options.RedisConnection;
            // The provider appends its own "cache:" segment, so keys become <prefix>cache:<taskId>.
            redis.KeyPrefix = options.RedisKeyPrefix;
            redis.CacheTtl = options.RedisCacheTtl;
        }));
    }

    private static void ConfigureDiscovery(ICronnerBuilder cronner, TestAppOptions options)
    {
        switch (options.Discovery)
        {
            case DiscoveryMode.Auto:
                // The entry assembly is scanned by default; opt the external jobs assembly in as well.
                cronner.AutoDiscoverFromAssembly(typeof(ExternalMaintenanceJobs));
                break;

            case DiscoveryMode.Filtered:
                cronner
                    .AutoDiscoverFromAssembly(typeof(ExternalMaintenanceJobs))
                    .AutoDiscoverFromType(typeof(IScheduledJob));
                break;

            case DiscoveryMode.Off:
                // Only the Sched(...) lambdas survive this.
                cronner.DisableAutoDiscovery();
                break;

            default:
                throw new InvalidOperationException($"Unhandled discovery mode '{options.Discovery}'.");
        }
    }
}
