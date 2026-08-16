# DotnetCronner (by Malte)

[![Build](https://img.shields.io/github/actions/workflow/status/nfMalde/DotnetCronner/pr.yml?branch=main&label=build&style=flat-square)](https://github.com/nfMalde/DotnetCronner/actions)
[![NuGet](https://img.shields.io/nuget/v/DotnetCronner?style=flat-square)](https://www.nuget.org/packages/DotnetCronner)
[![Downloads](https://img.shields.io/nuget/dt/DotnetCronner?style=flat-square)](https://www.nuget.org/packages/DotnetCronner)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Donate](https://img.shields.io/badge/donate-paypal-blue?style=flat-square)](https://www.paypal.com/donate/?hosted_button_id=SVZHLRTQ6H4VL)

DotnetCronner is a simple, free, open-source cron job and background task scheduler for .NET. It lets
you queue and manage scheduled work with a pluggable store, so you are never locked into a particular
database or ORM. Register tasks with a cron expression, run them in the background, and manage them
in-process — there is no separate dashboard app to host or secure.

- Cron scheduling via a small, zero-dependency parser (5 fields, or 6 with seconds).
- Two ways to register tasks: a `[CronnerTask]` attribute or fluent `Sched<T>(...)` lambdas.
- Pluggable persistence through a single `ICronnerStore` interface (in-memory by default).
- Redis and Entity Framework Core stores as separate packages; Redis also works as a second-level cache.
- Per-task priority and concurrency policy; a task never overlaps with itself by default.
- Lifecycle hooks, configurable DI scopes, and build-time cron validation.

## Why DotnetCronner?

I looked through a lot of cron managers for .NET, and most were packed with features and shipped a heavy
dashboard. All I wanted was a simple scheduler for my app with a ready-to-use API client to trigger,
manage, and delete jobs straight from my own admin panel — so I built one that's easy to use and easy to
extend. Instead of a bloated core it's a plugin design, and I'll open up more extension points over time;
the current state should give you a solid start.

The other thing I kept running into: I usually had to pull in some ORM just to make the scheduler
persistent. So DotnetCronner ships two built-in stores as separate packages, on top of a small
`ICronnerStore` interface that takes only a few lines to implement — use whatever ORM, database, or file
store you like. Take my stores and second-level caches, or write your own; switching between them is no
headache.

This is my first time working this deeply with cron scheduling, so I started with what my own projects
needed most. It isn't the finished product — and yes, it stays free.

## Packages

Each package is versioned and released independently.

| Package | Version | Downloads | Purpose |
| --- | --- | --- | --- |
| [DotnetCronner](https://www.nuget.org/packages/DotnetCronner) | ![NuGet](https://img.shields.io/nuget/v/DotnetCronner?style=flat-square) | ![Downloads](https://img.shields.io/nuget/dt/DotnetCronner?style=flat-square) | Core engine, in-memory store, build-time analyzer |
| [DotnetCronner.Abstractions](https://www.nuget.org/packages/DotnetCronner.Abstractions) | ![NuGet](https://img.shields.io/nuget/v/DotnetCronner.Abstractions?style=flat-square) | ![Downloads](https://img.shields.io/nuget/dt/DotnetCronner.Abstractions?style=flat-square) | Interfaces, models, `[CronnerTask]` attribute |
| [DotnetCronner.Stores.Redis](https://www.nuget.org/packages/DotnetCronner.Stores.Redis) | ![NuGet](https://img.shields.io/nuget/v/DotnetCronner.Stores.Redis?style=flat-square) | ![Downloads](https://img.shields.io/nuget/dt/DotnetCronner.Stores.Redis?style=flat-square) | Redis store and second-level cache |
| [DotnetCronner.Stores.EntityFrameworkCore](https://www.nuget.org/packages/DotnetCronner.Stores.EntityFrameworkCore) | ![NuGet](https://img.shields.io/nuget/v/DotnetCronner.Stores.EntityFrameworkCore?style=flat-square) | ![Downloads](https://img.shields.io/nuget/dt/DotnetCronner.Stores.EntityFrameworkCore?style=flat-square) | Entity Framework Core store |

## Requirements

- .NET 10 SDK / runtime.
- Optionally StackExchange.Redis (Redis package) or Entity Framework Core (EF Core package) — pulled in
  transitively when you install those packages.

## Install

The core package is all you need to get started:

```bash
dotnet add package DotnetCronner
```

```powershell
NuGet\Install-Package DotnetCronner
```

Add a store package only if you need durable or distributed persistence:

```bash
dotnet add package DotnetCronner.Stores.Redis
dotnet add package DotnetCronner.Stores.EntityFrameworkCore
```

## Enable it

Register the services, then configure stores and tasks after the host is built:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddDotnetCronner();

var app = builder.Build();

app.UseDotnetCronner(cronner => cronner
    // .UseRedisAsStore("localhost:6379")        // or .UseEntityFrameworkStore<AppDbContext>()
    .Sched<ReportJobs>(
        x => x.SendAsync(x.HasParam<IReportService>(), x.HasParam<CancellationToken>()),
        o => o.WithCron("0 * * * *").WithPrio(CronnerTaskPriority.High)));

app.Run();
```

You can also configure everything up front in `AddDotnetCronner(cronner => ...)`; the same builder is
available in both places.

## Configuration

Configure via `AddDotnetCronner(c => c.Configure(o => ...))` or `app.UseDotnetCronner(c => c.Configure(...))`.

| Option | Default | Description |
| --- | --- | --- |
| `TimeZone` | UTC | Time zone cron expressions are evaluated in |
| `PollingInterval` | 1s | How often the store is polled for due tasks |
| `MaxConcurrentTasks` | processor count | Global concurrent execution limit |
| `LockTtl` | 1 min | Lock validity window. A running task renews its claim every `LockTtl`/2, so long jobs keep their lock; a task is treated as stalled and reclaimable only after a worker stops renewing for longer than this (e.g. a crash) |
| `DefaultMaxRetries` / `RetryDelay` | 0 / 0 | Automatic retry on failure |
| `ScanEntryAssembly` | true | Scan the entry assembly for `[CronnerTask]` methods |

## Usage

### Attribute tasks

```csharp
public class ReportJobs
{
    [CronnerTask(cronstring: "*/5 * * * *", Priority = CronnerTaskPriority.Normal)]
    public Task SendAsync(IReportService reports, CancellationToken ct) => reports.SendAsync(ct);
}
```

If `id` is omitted it is derived from the fully qualified `Type.Method` name. Registering two tasks with
the same id throws. Attribute parameters are resolved from dependency injection, with `CancellationToken`
bound to the task's token.

By default `[CronnerTask]` methods in the entry assembly are discovered automatically. To control exactly
what gets registered:

```csharp
app.UseDotnetCronner(c => c
    .DisableAutoDiscovery()                        // stop scanning the entry assembly
    .AutoDiscoverFromAssembly(typeof(ReportJobs))  // scan the assembly containing this type
    .AutoDiscoverFromType(typeof(IScheduledJob))); // only types implementing this interface / base class
```

`AutoDiscoverFromAssembly` also accepts `Assembly` values; `AutoDiscoverFromType` filters types across
every scanned assembly.

### Lambda tasks and `HasParam`

Inside a `Sched` lambda, `x.HasParam<T>()` declares a parameter that is supplied at run time: a
`CancellationToken` is bound to the task's token, anything else is resolved from the scoped service
provider. Ordinary values (`"world"`, `42`, …) are captured as literals.

```csharp
app.UseDotnetCronner(c => c.Sched<ReportJobs>(
    x => x.SendAsync(x.HasParam<IReportService>(), x.HasParam<CancellationToken>()),
    o => o.WithCron("0 * * * *")));
```

A second `HasParam` overload takes a factory so you can resolve the value yourself from the execution
scope's provider — useful for pulling a value off a scoped accessor:

```csharp
app.UseDotnetCronner(c => c.Sched<TenantJobs>(
    x => x.Run(x.HasParam<Tenant>(sp => sp.GetRequiredService<ITenantAccessor>().Current)),
    o => o.WithCron("*/10 * * * *")));
```

### Stores

```csharp
// Redis backing store (package: DotnetCronner.Stores.Redis)
app.UseDotnetCronner(c => c.UseRedisAsStore("localhost:6379"));

// EF Core backing store (package: DotnetCronner.Stores.EntityFrameworkCore)
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(cs));
app.UseDotnetCronner(c => c.UseEntityFrameworkStore<AppDbContext>());

// Custom store — implement ICronnerStore
app.UseDotnetCronner(c => c.UseStore<MyStore>());
```

Bring your own persistence (Dapper, NHibernate, a UnitOfWork, …) by implementing `ICronnerStore` —
see [writing a custom store](docs/custom-store.md). Abstractions ships a ready-made `CronnerJobEntity`
(a mutable class with `virtual` properties and `ToDomain`/`From`/`Apply` helpers) you can map or subclass
instead of hand-rolling one.

`AppDbContext` implements `ICronnerDbContext` and calls `modelBuilder.ApplyCronnerModel()` in
`OnModelCreating`. Configuring more than one store throws. For creating the `CronnerJobs` table with EF
Core migrations (for both a `DbContext` in your main project and one in a separate class library), see
[the migrations guide](src/DotnetCronner.Stores.EntityFrameworkCore/MIGRATIONS.md).

### Second-level cache

```csharp
app.UseDotnetCronner(c => c
    .UseEntityFrameworkStore<AppDbContext>()
    .UseSecondLevelCache(cache => cache.UseRedisCacheProvider("localhost:6379")));
```

### Concurrency policy

Set per task with `.WithConcurrency(...)` or `[CronnerTask(...)] { Concurrency = ... }`:

- `DropAndForget` (default) — skip an occurrence that fires while a previous run is still executing.
- `Queue` — run the missed occurrence immediately after the current one finishes (never overlapping).
- `Concurrent` — allow overlapping runs.

### Dependency injection and scopes

By default each run executes in a fresh scope created from your application's service provider, so scoped
services behave exactly as they do in a request. To run against an isolated container instead — to avoid
sharing scoped services with web requests, or to give jobs their own dependencies — use
`WithDedicatedDI`. You supply the service collection, and the discovered job classes are registered
into it with the lifetime you choose (`Scoped` by default; `Singleton`, `Transient`, …):

```csharp
app.UseDotnetCronner(cronner => cronner
    .WithDedicatedDI(services =>
    {
        services.AddSingleton<IEmailClient, SmtpEmailClient>();
        services.AddDbContextFactory<JobsDbContext>(o => o.UseNpgsql(cs));
    }, jobLifetime: ServiceLifetime.Singleton)
    .Sched<MailJobs>(x => x.SendDigestAsync(x.HasParam<IEmailClient>(), x.HasParam<CancellationToken>()), "0 8 * * *"));
```

A fresh scope is still created per run. DI-registered `ICronnerTaskHook`s are resolved from the same
provider, so register them in the dedicated collection when using dedicated services.

### Lifecycle and lock hooks

Hook into task execution for logging, metrics, notifications, or custom error handling. A hook that
throws is logged and ignored, so it never breaks a task. There are eight events:

| Event | Fires |
| --- | --- |
| `OnStart` | before the task method runs |
| `OnSuccess` | after a successful run (`ctx.Duration` is set) |
| `OnFail` | after the task throws (`ctx.Exception` is set) |
| `OnCancel` | after the task is cancelled |
| `OnLockAcquire` | when the execution lock is claimed |
| `OnKeepAlive` | on each lock renewal (keepalive) while the task runs |
| `OnLockRelease` | when the lock is released |
| `OnLockLost` | when the lock is lost mid-run and the run is cancelled |
| `OnTotalProgressChange` | when a task reports total progress (`ctx.TotalProgress`) |
| `OnProgressScopeOpened` | when a task opens a progress scope (`ctx.ProgressScope`) |
| `OnScopeProgress` | when a task reports progress to a scope |
| `OnProgressScopeClosed` | when a task closes a progress scope |

**Global**, as a delegate:

```csharp
app.UseDotnetCronner(cronner => cronner
    .OnStart(ctx => { logger.LogInformation("Starting {Id}", ctx.Job.Id); return Task.CompletedTask; })
    .OnSuccess(ctx => { metrics.RecordSuccess(ctx.Job.Id, ctx.Duration); return Task.CompletedTask; })
    .OnFail(ctx => alerts.NotifyAsync(ctx.Job.Id, ctx.Exception!))
    .OnLockLost(ctx => alerts.LockLostAsync(ctx.Job.Id))
    .Sched<ReportJobs>(x => x.SendAsync(x.HasParam<IReportService>(), x.HasParam<CancellationToken>()), "0 * * * *"));
```

**Per schedule** — the same `On*` methods (plus `WithHook`) on the schedule options, so a hook applies to
one task only:

```csharp
cronner.Sched<ReportJobs>(x => x.SendAsync(...), o => o
    .WithCron("0 * * * *")
    .OnSuccess(ctx => metrics.RecordSuccess(ctx.Job.Id, ctx.Duration))
    .OnLockLost(ctx => alerts.LockLostAsync(ctx.Job.Id)));
```

**As a method call with `HasParam`** — every `On*` also accepts a target type and a method-call lambda
(the same shape as `Sched`), resolving arguments with `HasParam<T>()` / `HasParam<T>(sp => ...)`:

```csharp
cronner.OnFail<AlertHandler>(h => h.Notify(h.HasParam<ISlack>()))
       .Sched<ReportJobs>(x => x.SendAsync(...), o => o
           .WithCron("0 * * * *")
           .OnStart<AuditHandler>(h => h.RecordStartAsync(h.HasParam<IAudit>())));
```

Or a reusable hook implementing `ICronnerTaskHook`, registered globally (`AddHook<T>()` / `AddHook(instance)`)
or per schedule (`WithHook<T>()` / `WithHook(instance)`), or directly in DI:

```csharp
public sealed class LoggingHook(ILogger<LoggingHook> logger) : ICronnerTaskHook
{
    public Task OnFailAsync(CronnerTaskContext ctx)
    {
        logger.LogError(ctx.Exception, "Task {Id} failed after {Duration}", ctx.Job.Id, ctx.Duration);
        return Task.CompletedTask;
    }
}

builder.Services.AddScoped<ICronnerTaskHook, LoggingHook>();   // or cronner.AddHook<LoggingHook>()
```

`ICronnerTaskHook` has default method implementations, so override only the events you need. **Each hook
invocation runs in its own fresh DI scope** — resolve scoped services through `ctx.Services` or
`ctx.HasParam<T>()`. `CronnerTaskContext` exposes the `Job`, the scoped `Services`, `HasParam<T>()`, the
`CancellationToken`, the `Duration`, and the `Exception` on failure.

For database work inside a hook, resolve your own scoped unit-of-work that way — **don't call
`ICronnerStore` from a hook.** It has no per-hook session, and creating the scope alone opens no
connection, so hooks that don't touch a database cost nothing. `OnKeepAlive` in particular is fired
without blocking the lock renewal, so a slow keepalive hook can never delay a renewal or cost you the
lock — no matter how long the task runs.

### Reporting progress

A running task can report progress by pulling `ICronnerJobContext` from DI (constructor injection or
`HasParam<ICronnerJobContext>()`). Report overall progress directly, and open a scope for a
subtask/category whose progress is tracked independently. Progress is a `0..1` fraction by convention
(not enforced). Each report raises the matching hook.

```csharp
public sealed class ImportJob
{
    public async Task Run(ICronnerJobContext ctx, CancellationToken ct)
    {
        ctx.Progress(0.1m);                                   // total progress (fire-and-forget)
        await using (var files = ctx.OpenProgressScope("files"))
        {
            files.Progress(0.5m);                             // subtask / category progress
            await files.ProgressAsync(1.0m);                  // awaitable variant
        }                                                     // dispose closes the scope
        await ctx.ProgressAsync(1.0m);
    }
}
```

Observe it with the progress hooks (global or per schedule) — `ctx.TotalProgress` on total changes, and
`ctx.ProgressScope` (`.Id`, `.Category`, `.Value`) on scope events:

```csharp
cronner.OnTotalProgressChange(ctx => hub.PushAsync(ctx.Job.Id, ctx.TotalProgress))
       .OnScopeProgress(ctx => hub.PushAsync(ctx.Job.Id, ctx.ProgressScope!.Category, ctx.ProgressScope!.Value));
```

Both `Progress` (fire-and-forget) and `ProgressAsync` (awaits the hooks) are available on the context and
on a scope; fire-and-forget reports are drained before the terminal `OnSuccess`/`OnFail` hook runs.

### Managing tasks — `ICronnerClient`

There is no bundled dashboard. Inject `ICronnerClient` and expose management through your own, already
secured, endpoints:

```csharp
app.MapGet("/tasks", (ICronnerClient c, CronnerTaskState? state, int offset = 0, int limit = 50)
    => c.GetTasksAsync(state, offset, limit));
app.MapGet("/tasks/{id}", (ICronnerClient c, string id) => c.GetTaskByIdAsync(id));
app.MapPost("/tasks/{id}/run", (ICronnerClient c, string id) => c.ScheduleTaskAsync(id));
app.MapPost("/tasks/{id}/cancel", (ICronnerClient c, string id) => c.CancelTaskAsync(id));
```

## Validation

Cron mistakes are caught as early as possible:

- At build time, the bundled Roslyn analyzer flags problems on `[CronnerTask]` in your editor and in CI:
  - `DC0001` (error): an invalid cron expression, e.g. `[CronnerTask(cronstring: "not a cron")]`.
  - `DC0002` (warning): two `[CronnerTask]` attributes sharing the same explicit id.
- At registration, invalid cron strings — from attributes or `Sched(...)` lambdas — throw immediately at
  startup, naming the offending task, rather than failing silently later.

The analyzer ships inside the `DotnetCronner` package, so no extra reference is needed.

## Limitations and roadmap

Running DotnetCronner across **multiple instances or nodes is not officially supported yet.** The Redis
and EF Core stores already include the claiming primitives this needs (per-job locks and optimistic
concurrency), but coordinated multi-node operation has not been fully validated. It is on the roadmap —
contributions are very welcome, so feel free to open a PR.

For now, run a single scheduler instance (other instances of your app can still use `ICronnerClient` to
read and trigger tasks against the shared store).

Regardless of instance count, a running task holds an execution lock that the scheduler keeps alive by
renewing it every `LockTtl`/2. A long-running job therefore keeps its claim for as long as it runs — it is
never mistaken for a stalled worker and re-run. A task only becomes reclaimable after its worker stops
renewing for longer than `LockTtl` (a crash or a very long GC pause); when that reclaim happens it is
logged as a warning. If a worker loses a lock mid-run, its own execution is cancelled so the task is never
running twice at once.

## Changelog

Each package keeps its own changelog. See [CHANGELOG.md](CHANGELOG.md) for the index.

## Contributing and releases

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing, and the per-package release process.
Packages are released **independently** and only by a maintainer running the **Release Package** workflow
(Actions → Run workflow): pick the package and `patch`/`minor`/`major` and it computes the next version
from the last release tag, so there's nothing to look up. Merging a PR never publishes, and the NuGet push
waits on an approval gate — so a wrong version or a rogue release can't slip through. The git tag is
created by the workflow when the release actually publishes.

## Contribute / Donations

If you have any ideas to improve DotnetCronner, feel free to send a pull request.

If you like my work and want to support me (or want to buy me a coffee/beer), PayPal donations are more
than appreciated.

[![Donate](https://img.shields.io/badge/donate-paypal-blue?style=flat-square)](https://www.paypal.com/donate/?hosted_button_id=SVZHLRTQ6H4VL)

## License

MIT — see [LICENSE](LICENSE).