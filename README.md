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
- Twelve lifecycle, lock, and progress hooks (global or per schedule), plus in-process progress reporting
  via `ICronnerJobContext`.
- Configurable DI scopes, an execution-lock keepalive for long-running jobs, and build-time cron validation.

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
| `LockTtl` | 1 min | Lock validity window. A running task renews its claim every `LockTtl`/2, so long jobs keep their lock; a task is treated as stalled and reclaimable only after a worker stops renewing for longer than this (e.g. a crash). If renewals cannot be confirmed (store unreachable) the run is abandoned *before* this window lapses — see [Execution semantics](#execution-semantics). Size it generously: the abandon margin is ≈`LockTtl`/8, so `LockTtl` should be at least 8× the longest time your job needs to honour its cancellation token (plus any clock skew between nodes) |
| `KeepAliveInterval` | `null` (= `LockTtl`/2) | Pins how often the lock renews and `OnKeepAlive` fires, independently of `LockTtl` (also via `WithKeepAliveInterval(...)`). Keep it at or below `LockTtl`/2 (the scheduler warns above that, and logs an error at or above `LockTtl`). The effective values are exposed to hooks as `ctx.LockTtl` / `ctx.KeepAliveInterval` |
| `OneOffRetentionCount` | 0 (keep all) | Keep only the newest N finished one-off (enqueued) instances per definition (also via `WithOneOffRetention(N)`) |
| `ExecutionHistoryRetentionCount` | 0 (off) | Record per-run execution history, keeping the newest N runs per task (also via `WithExecutionHistory(N)`). Read with `ICronnerClient.GetExecutionsAsync(...)` |
| `HookScope` | `Shared` | Default scope for terminal hooks: `Shared` (the job's scope) or `Isolated` (own fresh scope). Override per hook via `AddHook`/`WithHook` |
| `OnInvalidSchedule` | `MarkFailed` | A cron that parses but never fires: `MarkFailed` (mark that task Failed, keep the rest) or `Throw` (fail host startup) |
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

// Secured Redis — auth + TLS travel in the connection string…
app.UseDotnetCronner(c => c.UseRedisAsStore("myhost:6380,user=cronner,password=SECRET,ssl=true,sslHost=myhost"));
// …or via ConfigurationOptions for full control (SslProtocols, cert callbacks, …):
app.UseDotnetCronner(c => c.UseRedisAsStore(o => o.ConfigurationOptions = new ConfigurationOptions
{
    EndPoints = { "myhost:6380" }, User = "cronner", Password = "SECRET", Ssl = true, SslHost = "myhost",
}));
// …or bring your own multiplexer: o.ConnectionMultiplexerFactory = sp => existingMultiplexer;
// …or configure entirely from DI — e.g. pull the (secured) string from IConfiguration:
app.UseDotnetCronner(c => c.UseRedisAsStore((sp, o) =>
    o.Configuration = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")));

// EF Core backing store (package: DotnetCronner.Stores.EntityFrameworkCore)
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(cs));
app.UseDotnetCronner(c => c.UseEntityFrameworkStore<AppDbContext>());

// Custom store — implement ICronnerStore
app.UseDotnetCronner(c => c.UseStore<MyStore>());

// ...or built per scheduler operation, when the store holds a DbContext,
// ORM session or connection that must not be shared across overlapping work
app.UseDotnetCronner(c => c.UseStore<MyStore>(CronnerStoreLifetime.Scoped));
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

Set per task with `.WithConcurrency(...)` or the attribute's `Concurrency` property
(`[CronnerTask(cronstring: "*/5 * * * *", Concurrency = CronnerConcurrencyMode.Queue)]`):

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
throws is logged and ignored, so it never breaks a task. There are twelve events:

| Event | Fires |
| --- | --- |
| `OnStart` | before the task method runs |
| `OnSuccess` | after a successful run (`ctx.Duration` is set) |
| `OnFail` | after the task throws (`ctx.Exception` is set) |
| `OnCancel` | after the task is cancelled |
| `OnLockAcquire` | when the execution lock is claimed |
| `OnKeepAlive` | on each lock renewal (keepalive) while the task runs |
| `OnLockRelease` | when the lock is released |
| `OnLockLost` | when the lock is lost mid-run (a renewal was refused, or the lease could not be confirmed before it lapsed) and the run was cancelled |
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

`ICronnerTaskHook` has default method implementations, so override only the events you need. By default the
**terminal lifecycle hooks** (`OnStart`/`OnSuccess`/`OnFail`/`OnCancel`) run in the **job's execution
scope**, so a hook's `ctx.HasParam<T>()` resolves the *same* scoped instances the job used — e.g. the job
writes a summary into a scoped service and the terminal hook reads it back. **Lock and progress hooks**
fire outside the job's execution, so each runs in its **own** fresh scope. Resolve scoped services through
`ctx.Services` or `ctx.HasParam<T>()`; `CronnerTaskContext` exposes the `Job`, `Services`, `HasParam<T>()`,
`CancellationToken`, `Duration`, the `Exception` on failure, and the per-run **`ExecutionId`**.

**The execution id.** Every run has an id — `ctx.ExecutionId` in every hook of the run (from `OnLockAcquire`
through the terminal event) and `ICronnerJobContext.ExecutionId` in the job body — and it is the `Id` of
that run's execution-history row. It exists whether or not history is persisted, and it is the join key the
scheduler's record and your own per-run record (a log file, a progress label, a foreign key) share, so your
table can shrink to what only it can hold. **A retry is a new execution**: each attempt gets its own id (and
`Attempt` increments on the history row). The context also carries the **effective lock settings**,
`ctx.LockTtl` and `ctx.KeepAliveInterval`, so a consumer that tracks heartbeats can derive its staleness
threshold from the values in force instead of hardcoding one (a run without a heartbeat for a few multiples
of `KeepAliveInterval` is stalled; after `LockTtl` it is reclaimable by another instance).

**Hook scope — per hook.** Scope applies **only to the terminal hooks** (`OnStart`/`OnSuccess`/`OnFail`/
`OnCancel`), and you set it **per hook** on registration:

```csharp
cronner.AddHook<AuditHook>(CronnerHookScope.Isolated);   // this hook: own scope, no contention with the job's UoW
cronner.AddHook<LoggingHook>();                           // this hook: inherits the default (Shared)
// per schedule: .Sched<Job>(..., o => o.WithHook<TxHook>(CronnerHookScope.Isolated))
```

A hook with no explicit scope inherits `HookScope` (default `Shared` = the job's scope; set it via
`Configure` to flip the default to `Isolated`). `Shared` lets a hook's `ctx.HasParam<T>()` resolve the
*same* scoped instances the job used; `Isolated` gives the hook its own fresh scope — use it when the job's
scope holds a single-session unit of work (one `DbContext`/`ISession`) a hook writing on the same scope
would contend with. In an isolated hook, pass job data across with the run-state bag rather than shared
scoped services. **`OnKeepAlive`, the other lock hooks, and progress hooks always run in their own fresh
scope regardless** — `OnKeepAlive` fires detached and concurrently with the running job, so sharing the
job's scope (and its session) would be a bug; it reads the run-state bag for job data without ever touching
the job's scope.

**Run-state bag.** For scope-independent data flow, the job stashes values the hooks read back:
`ctx.Set(value)` from the job (`ICronnerJobContext`), `ctx.Get<T>()` / `ctx.TryGet<T>(out …)` from a hook
(`CronnerTaskContext`). The bag is per **run** (concurrent runs never share) and is visible to `OnKeepAlive`
and the terminal hooks — including `OnFail` — regardless of hook scope. (`OnStart` fires before the body, so
it can't see values the job sets.) This is the clean way to build a teardown/notification summary as pure
data: the job materializes it, the hook just reads it — no need to keep the job's session alive.

> Retries and `OnFail`: with `DefaultMaxRetries > 0`, `OnFail` fires on **each** failed attempt (not once
> after retries are exhausted). Check `ctx.WillRetry` — it's `true` while attempts remain and `false` on the
> final failure — to alert only once per incident.

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

Each progress report can also carry a **custom payload** — any object — delivered to the hook as
`ctx.ProgressPayload` for that report. It works for both total and scope progress and is the place for
per-report detail a scope's `Category` can't hold (a current-step name, an item id, a partial result):

```csharp
ctx.Progress(0.4m, "importing orders");                 // total: ctx.ProgressPayload == "importing orders"
files.Progress(0.5m, new { file = "part-3.csv" });      // scope: ctx.ProgressPayload is the anonymous object
// hook: cronner.OnTotalProgressChange(ctx => hub.PushAsync(ctx.Job.Id, ctx.TotalProgress, ctx.ProgressPayload))
```

Both `Progress` (fire-and-forget) and `ProgressAsync` (awaits the hooks) are available on the context and
on a scope; fire-and-forget reports are drained before the terminal `OnSuccess`/`OnFail` hook runs. (Progress
hooks can also read the per-run state bag via `ctx.Get<T>()`, exactly like the terminal hooks.)

### One-off jobs — enqueue with a payload

Beyond recurring cron tasks, you can enqueue a **single** run of a registered task carrying a typed
payload. Define an enqueue-only task (a `[CronnerTask]` with **no cron**) whose payload is an ordinary
parameter — everything else still resolves from DI:

```csharp
public sealed record ImportPayload(int BatchSize, string Source);

public class ImportJobs
{
    [CronnerTask("import:run", Description = "One-off import")]   // no cron = enqueue-only
    public Task RunAsync(ImportPayload payload, IImporter importer, CancellationToken ct)
        => importer.RunAsync(payload.Source, payload.BatchSize, ct);
}
```

```csharp
// Enqueue it now (or at a future time); returns the one-off instance id.
string id = await client.EnqueueAsync("import:run",
    new ImportPayload(500, "steam"), runAt: null);
```

The payload is JSON-serialized as its **declared** type (cycle-safe and proxy-safe — pass plain DTOs, not
lazy-loading ORM entities) and delivered to the parameter whose type matches. The instance runs **once**
and then completes; enable **retention** with `WithOneOffRetention(N)` to keep only the newest N finished
instances per definition. `CronnerJob.Kind` / `DefinitionId` distinguish a one-off instance from its
recurring definition.

### Managing tasks — `ICronnerClient`

There is no bundled dashboard. Inject `ICronnerClient` and expose management through your own, already
secured, endpoints:

```csharp
// Store rows (tasks that have run); GetRegisteredTasks() lists every definition incl. never-run ones.
app.MapGet("/tasks", (ICronnerClient c, CronnerTaskState? state, int offset = 0, int limit = 50)
    => c.GetTasksAsync(state, offset, limit));
app.MapGet("/registered", (ICronnerClient c) => c.GetRegisteredTasks());
app.MapGet("/tasks/{id}", (ICronnerClient c, string id) => c.GetTaskByIdAsync(id));
app.MapPost("/tasks/{id}/run", (ICronnerClient c, string id) => c.TriggerNowAsync(id));    // run now, ignoring cron
app.MapPost("/tasks/{id}/cancel", (ICronnerClient c, string id) => c.CancelTaskAsync(id)); // cancel running or unschedule
app.MapGet("/tasks/{id}/history", (ICronnerClient c, string id) => c.GetExecutionsAsync(id, 20)); // recent runs
```

`TriggerNowAsync` is the unambiguous "run now" (`ScheduleTaskAsync` is an alias). `CancelTaskAsync` cancels
a **running** execution via its token, or unschedules a pending one. `GetRegisteredTasks()` returns the
in-memory definitions (with `Description`), so an admin screen can list manual/enqueue-only jobs that have
never produced a store row.

### Execution history

Enable it with `WithExecutionHistory(keepPerTask)` (off by default). Each run records a `Running` entry when
it starts and finalizes it to `Succeeded` / `Failed` / `Cancelled` when it ends; older entries are pruned to
the per-task cap. `GetExecutionsAsync(taskId, limit)` returns them newest first. Each entry carries the
start/finish times, `Duration` (computed `FinishedAt - StartedAt`), status, attempt, error, the owning
scheduler instance (`Owner` — "which node ran this"), and its `Id` — the run's `ExecutionId` (see the hooks
section). A retry is a **new** execution with a new `Id` and the next `Attempt`. The EF Core store persists
history in a `CronnerJobExecutions` table (added in 0.0.5 — generate a migration if you upgrade from before
that); the Redis and in-memory stores need no schema step. The full model, lifecycle, and store extensibility
are documented in **[docs/execution-history.md](docs/execution-history.md)**.

**Keep application data in your own store.** Execution history records *an execution*, not a general log or a
place for business data. To attach your own per-run data (a summary, a log-file reference), keep it in your
own store keyed by the run's `ExecutionId` — the same id the job and every hook of the run see:

```csharp
myRunStore.Save(ctx.ExecutionId, new MySummary(...));   // your store, correlated by the execution id
```

> The older `ctx.SetExecutionData(...)` / `TryGetExecutionData<T>()` and the execution record's `Data` slot
> are **deprecated** (they still work) and will be removed in a future release — migrate to the pattern above.
> The `DotnetCronner.Sample.WebApi` sample shows it: a `RunSummaryStore` behind `GET /runs`.

**Self-consistent history, even after a crash.** A run whose owner dies mid-flight cannot finalize its own
row. The scheduler closes such orphans the next time the task runs: once it holds the task's lock, any
older row of that task still marked `Running` belongs to a run that will never finish, so it is finalized
as `Failed` with `Error = CronnerExecutionErrors.Orphaned` (`ICronnerStore.FinalizeOrphanedExecutionsAsync`).
A run the scheduler abandons itself because it lost (or could no longer confirm) its lock finalizes its
own row as `Cancelled` with `Error = CronnerExecutionErrors.LockLost`. Two residuals remain, by design:
`Concurrent`-mode tasks legitimately overlap, so their `Running` rows are never swept; and a task that
never runs again keeps its last `Running` row (there is no startup sweep, because on a multi-instance
deployment another node may legitimately be running the task right then).

### Execution semantics

- **`OnFail` fires per attempt, not per run.** With `DefaultMaxRetries > 0` each failed attempt fires it;
  use `ctx.WillRetry` to act only on the final failure. Each attempt is its own execution (own
  `ExecutionId`, `Attempt` + 1 on the history row).
- **One run per task across processes** is enforced by the store's execution lock: while one instance holds
  a task's claim, no other instance can claim it. The claim is an atomic, owner-conditional write on the
  store (a conditional `UPDATE` that re-asserts eligibility in EF Core, `SET NX` on a per-task key in Redis);
  renewal and release are owner-conditional too, and no other write — not `TriggerNowAsync`, not seeding at
  startup, not `CancelTaskAsync` — ever touches lock fields, so a stale snapshot written back can never free
  someone else's claim. The scheduler also claims only as many due tasks as it has free workers, so a claim
  never waits in a queue (without a heartbeat) past its lease, and it never starts a task that is already
  running in the same process. `DropAndForget` / `Queue` govern missed-occurrence catch-up within the owning
  worker; `Concurrent` is the exception — it releases the claim up front to allow parallel runs. **This is
  tested, not asserted:** `StoreLockContractTests` (8 instances racing for 40 due tasks, each handed out
  exactly once; renew/release only by the owner; a stale `Upsert` never clears a foreign lock) and
  `SchedulerExclusivityTests` (two schedulers on one store: every task runs once and never concurrently;
  long runs under a short TTL; an instance that loses its store mid-run is stopped before the survivor
  reclaims; a cancel from the other instance) run in `tests/DotnetCronner.Tests` against the in-memory and
  SQLite stores and in `tests/DotnetCronner.IntegrationTests` against real **PostgreSQL**, **SQL Server**
  (READ COMMITTED with and without snapshot) and **Redis** on every CI build. For a custom store, the
  contract those tests check is spelled out in [`docs/custom-store.md`](docs/custom-store.md) — and you can
  run the very same suites against it.
- **The lease rule — what happens when the store cannot answer.** A keepalive renewal answered `false`
  (the claim was reclaimed, released, or the task cancelled) is a definitive loss and the run is cancelled at
  once. A renewal the store cannot answer — it throws, or does not answer in time — is **not** a lost lock:
  the lease is still the worker's until the expiry that was last confirmed. The worker keeps the run alive
  and retries at a tighter cadence while that expiry is ahead, and abandons the run (cancels it, fires
  `OnLockLost`) as soon as the next retry could not land before the lease lapses — so the job is told to
  stop **before** another instance is able to claim the task, never after. With the default `LockTtl`/2
  cadence a single failure leaves three retries of slack (with a 60s TTL: renew at 30s, retries at 37.5s,
  45s, 52.5s, abandon at 52.5s — a 7.5s margin before anyone else can claim). The only way two runs can
  overlap is a job that ignores its `CancellationToken` for longer than that margin, or node clocks skewed
  by more than it (for the EF Core and in-memory stores, which compare `LockedUntilUtc` against the claiming
  node's clock; Redis expires the lock key server-side) — hence the sizing advice on `LockTtl`, and NTP.
  Store authors: **throw** when you cannot tell, return `false` only when the claim is definitively not the
  caller's; the scheduler owns the policy.
- **`CancelTaskAsync`** is state-dependent, not both at once: a task **running in this process** is
  signalled through its token (it stops if it honours the token); otherwise it is unscheduled in the store
  (`State = Cancelled`), and if it is running on **another instance** that run stops at its next keepalive
  (a store refuses to renew a cancelled task; the runner sees it as a cancel — `OnCancel`, not
  `OnLockLost`). The lock itself is never touched by a cancel.
- **`OnLockLost`** fires in exactly two situations, both after the run was cancelled: a keepalive renewal
  was refused (`RenewLockAsync` returned `false` and the task was not cancelled — the claim was reclaimed
  elsewhere), or the lease could not be confirmed before it lapsed (renewals kept throwing / hanging). The
  abandoning worker writes no task outcome (the reclaiming worker owns the schedule) but does finalize its
  own history row as `Cancelled` with `CronnerExecutionErrors.LockLost`.
- **A trigger while the task runs elsewhere.** `TriggerNowAsync` parks a trigger for a task running in the
  *same* process and fires it right after; for a task running on *another* instance the trigger is written to
  the store and is superseded by that run's own final write — it does not queue a second run.

## Validation

Cron mistakes are caught as early as possible:

- At build time, the bundled Roslyn analyzer flags problems on `[CronnerTask]` in your editor and in CI:
  - `DC0001` (error): an invalid cron expression, e.g. `[CronnerTask(cronstring: "not a cron")]`.
  - `DC0002` (warning): two `[CronnerTask]` attributes sharing the same explicit id.
- At registration, invalid cron strings — from attributes or `Sched(...)` lambdas — throw immediately at
  startup, naming the offending task, rather than failing silently later.

The analyzer ships inside the `DotnetCronner` package, so no extra reference is needed.

## Limitations and roadmap

**DotnetCronner is pre-1.0.** Expect a few more `0.0.x` releases and one or more previews before a stable
`1.0.0`. While on `0.x`, the public API may still change between releases as it settles and more extension
points open up, so pin your versions accordingly — `1.0.0` will follow once the surface has proven itself
in real use.

**Multiple instances / nodes.** Running several scheduler instances against one shared store is supported
and validated for the **EF Core store on PostgreSQL and SQL Server** and for the **Redis store** — the
contended-claim and two-scheduler suites described under [Execution semantics](#execution-semantics) run
against those backends in CI. The **in-memory store** is single-process by nature. A **custom store** gives
the same guarantee exactly when it implements the lock contract in [`docs/custom-store.md`](docs/custom-store.md)
(atomic eligibility-re-asserting claim, owner-conditional renew/release, lock-preserving upsert) — run the
shared contract tests against it to be sure. Two multi-instance caveats: `ICronnerClient`'s knowledge of
*running* tasks is per process (`CancelTaskAsync` on another node stops the run at its next keepalive; a
`TriggerNowAsync` for a task running elsewhere does not queue a second run), and the EF Core / in-memory
stores compare lock expiries against the claiming node's clock, so keep node clocks in sync (NTP) and size
`LockTtl` per the configuration table.

Regardless of instance count, a running task holds an execution lock that the scheduler keeps alive by
renewing it every `LockTtl`/2. A long-running job therefore keeps its claim for as long as it runs — it is
never mistaken for a stalled worker and re-run. A task only becomes reclaimable after its worker stops
renewing for longer than `LockTtl` (a crash, or a store outage long enough that the worker abandoned the
run itself first — see the lease rule); when that reclaim happens it is logged as a warning, and the dead
run's history row is closed as orphaned. If a worker loses a lock mid-run, its own execution is cancelled so
the task is never running twice at once.

## Samples

Two runnable samples live in [`samples/`](samples):

- **`DotnetCronner.Sample.WebApi`** — a minimal quickstart: attribute + lambda tasks, execution history
  (with the job's summary on the history row, augmented by a hook via `TryGetExecutionData`, keyed by
  `ExecutionId`), and a progress job whose reports carry custom payloads, over a handful of
  `ICronnerClient` endpoints (`/tasks`, `/tasks/{id}/history`, `/progress`).
- **[`CronTestApp`](samples/CronTestApp)** — a full harness that exercises every feature (all store modes
  incl. two hand-written stores that implement the full lock contract, all twelve hooks in every
  registration style, progress, the keepalive, lock loss and the lease rule live via a renewal-outage
  switch, discovery and DI modes), driven entirely by a `.env` file. It's a **Docker Compose** project
  (app + Redis + PostgreSQL, plus an optional **second scheduler instance** on the same store to watch
  "one run per task across processes" by hand) — requires Docker + Docker Compose: copy `.env.example` to
  `.env` and `docker compose up --build`.

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