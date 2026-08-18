# CronTestApp — a DotnetCronner test harness

An ASP.NET Core app that exercises **every** DotnetCronner feature: all store implementations, both
registration styles, all cron syntax variants, all priorities, all concurrency policies, the execution-lock
keepalive and lock loss, cancellation, progress reporting, all twelve hook events in all four registration
styles, both DI modes, and all discovery modes.

This sample lives in the DotnetCronner repo under `samples/CronTestApp`, and consumes the library by
**project reference** to `..\..\..\src`, so you are always testing the working copy — no packing, no NuGet
feed.

**Requirements: Docker + Docker Compose.** This is a **Docker Compose project** (app + Redis + PostgreSQL,
with the repo root as the build context), not a standalone `dotnet run` app — Compose is what wires the
scheduler up to Redis/PostgreSQL and points the app at them. Run it with `docker compose`.

Two knobs decide everything:

* **`.env`** — which store, which cache, which discovery mode, which DI mode, plus the scheduler options.
* **`docker-compose.yml`** — Redis and PostgreSQL, so whichever store you pick has something to talk to.

The real `.env` is git-ignored; copy the template first:

```powershell
cd samples/CronTestApp
copy .env.example .env      # cp .env.example .env  (bash)
```

The template defaults to `CRONNER_STORE=memory`, so `docker compose up` works without configuring Redis or
PostgreSQL (Compose still starts them, they're just unused until you switch `CRONNER_STORE`).

---

## Run it

```powershell
# From samples/CronTestApp (after copying .env.example → .env):
docker compose up -d --build
curl http://localhost:8080/config
```

That builds and starts everything (app + Redis + PostgreSQL). The app listens on `http://localhost:8080`.

`.env` is read by docker compose **and** by the app itself (`DotEnvFile`). Real environment variables win
over the file, which is how compose points the app at `redis:6379` / `Host=postgres` inside the network
while the file keeps `localhost` values for reference.

After changing `.env`, recreate the app container so it picks the new values up:

```powershell
docker compose up -d --force-recreate crontestapp
```

---

## What `.env` steers

| Variable | Values | What it tests |
| --- | --- | --- |
| `CRONNER_STORE` | `memory`, `redis`, `ef-postgres`, `ef-postgres-appcontext`, `custom`, `scoped`, `scoped-broken` | Every `ICronnerStore` path (see below) |
| `CRONNER_CACHE` | `none`, `redis` | `UseSecondLevelCache(c => c.UseRedisCacheProvider(...))` |
| `CRONNER_DISCOVERY` | `auto`, `filtered`, `off` | `AutoDiscoverFromAssembly` / `AutoDiscoverFromType` / `DisableAutoDiscovery` |
| `CRONNER_JOB_SERVICES` | `app`, `dedicated` | App provider vs. `WithDedicatedDI(...)` |
| `CRONNER_TIMEZONE` | `UTC`, `Europe/Berlin`, … | `CronnerOptions.TimeZone` (watch `nextRunUtc` move) |
| `CRONNER_POLLING_MS` | e.g. `1000` | `PollingInterval` |
| `CRONNER_MAX_CONCURRENT` | e.g. `8` | `MaxConcurrentTasks` |
| `CRONNER_LOCK_TTL_SECONDS` | e.g. `20` | `LockTtl`, and therefore the keepalive interval (`LockTtl`/2) |
| `CRONNER_KEEPALIVE_SECONDS` | `0` (= `LockTtl`/2), e.g. `10` | `WithKeepAliveInterval(...)` — pins the keepalive cadence independently of `LockTtl` |
| `CRONNER_ONEOFF_RETENTION` | `0` (keep all), e.g. `20` | `WithOneOffRetention(N)` — newest N finished one-off instances per definition |
| `CRONNER_EXEC_HISTORY` | `0` (off), e.g. `20` | `WithExecutionHistory(N)` — record execution history; see `GET /tasks/{id}/history` |
| `CRONNER_HOOK_SCOPE` | `shared` (default), `isolated` | `CronnerOptions.HookScope` — default DI scope for terminal hooks |
| `CRONNER_INVALID_SCHEDULE` | `mark-failed` (default), `throw` | `CronnerOptions.OnInvalidSchedule` — a never-firing cron marks the task Failed vs. fails startup |
| `CRONNER_MAX_RETRIES` / `CRONNER_RETRY_DELAY_SECONDS` | e.g. `2` / `5` | `DefaultMaxRetries` / `RetryDelay` |
| `CRONNER_SLOW_KEEPALIVE_MS` | `0` (off), e.g. `15000` | makes the global `OnKeepAlive` hook block — proves a slow hook cannot stretch the renewal cadence |
| `CRONNER_REDIS`, `CRONNER_REDIS_KEY_PREFIX`, `CRONNER_REDIS_CACHE_TTL_SECONDS` | | Redis store & cache options |
| `CRONNER_POSTGRES` | Npgsql connection string | EF Core stores |
| `CRONNER_CUSTOM_STORE_FILE` | path | Where the custom JSON store persists |

### The seven store modes

| `CRONNER_STORE` | Wiring under test | Needs |
| --- | --- | --- |
| `memory` | the default in-memory store | — |
| `redis` | `UseRedisAsStore(...)` | `redis` service |
| `ef-postgres` | `UseEntityFrameworkStore(o => o.UseNpgsql(...))` with the package's `CronnerDbContext` | `postgres` service |
| `ef-postgres-appcontext` | `UseEntityFrameworkStore<AppDbContext>()` — this app's own context implementing `ICronnerDbContext` + `ApplyCronnerModel()` | `postgres` service |
| `custom` | `UseStore<JsonFileCronnerStore>()` — a hand-written `ICronnerStore` over a JSON file | — |
| `scoped` | `UseStore<ScopedSqliteCronnerStore>(CronnerStoreLifetime.Scoped)` — a store holding one open SQLite connection, built per scheduler operation | — |
| `scoped-broken` | the same store as `Singleton`, so overlapping operations share one connection — **expected to fail**, see below | — |

#### Verifying the store lifetime

`scoped` and `scoped-broken` use the *same* store — the only difference is the lifetime it declares.
`ScopedSqliteCronnerStore` holds one open connection and does no internal locking, which is exactly the
shape of a `DbContext` or ORM session.

Run with several registered tasks so the poll loop overlaps running jobs:

- `CRONNER_STORE=scoped` — each scheduler operation gets its own instance and its own connection. Jobs
  schedule and run cleanly.
- `CRONNER_STORE=scoped-broken` — one instance is shared, so overlapping operations issue concurrent
  commands on one connection and SQLite raises a concurrent-access error. This is the same failure that
  appears as *"a command is already in progress"* (Npgsql) or *"a second operation was started on this
  context instance"* (EF Core), and it is why the lifetime is declared rather than assumed.

`scoped-broken` is a demonstration, not a configuration to copy.

The EF schema is created with `EnsureCreatedAsync()` at startup (throwaway-database shortcut; real apps
generate migrations and call `MigrateAsync()` — see `MIGRATIONS.md` in the EF store package).

---

## Endpoints

There is no bundled dashboard, so everything goes through `ICronnerClient` on this app's own endpoints
(`CronTestApp.http` has ready-made requests):

| Endpoint | Purpose |
| --- | --- |
| `GET /config` | the active `.env`-driven configuration |
| `GET /tasks?state=&offset=&limit=` | every registered task, with state, priority, next run, last error |
| `GET /registered` | every registered definition, incl. never-run / manual / enqueue-only ones |
| `GET /tasks/{id}` | one task |
| `GET /tasks/{id}/history?take=` | execution history for a task (needs `CRONNER_EXEC_HISTORY>0`), newest first |
| `POST /tasks/{id}/run` | trigger now — the only way manual tasks ever run |
| `POST /enqueue/notify` | enqueue a one-off with a typed payload (body: `{to,message,attempt}`) |
| `POST /tasks/{id}/cancel` | cancel a running task and unschedule it |
| `POST /tasks/{id}/steal-lock` | write a foreign lock owner into the store to force `OnLockLost` |
| `GET /activity?take=&jobId=` | **what the jobs actually did**, newest first |
| `GET /metrics` | per-task counters incl. lock events, filled exclusively by the hooks |
| `GET /progress` | live total/scope progress, rebuilt purely from the progress hooks |
| `GET /locks` | **measured** keepalive cadence per task vs. the expected `LockTtl`/2 |
| `GET /healthz` | liveness |

`/activity` is the fastest way to see behaviour; `/metrics` and `/progress` prove the hooks ran, since
nothing else writes to them.

---

## The task catalogue

Two dozen-odd tasks, each one there to prove something. Attribute tasks live in `CronTestApp/Jobs`, lambda
tasks are registered in `Configuration/CronnerSetup.cs`.

### `[CronnerTask]` attribute tasks

| Id | Cron | Demonstrates |
| --- | --- | --- |
| `attr:heartbeat` | `* * * * *` | classic 5-field cron |
| `attr:every-5-seconds` | `*/5 * * * * *` | 6-field cron with seconds, `Priority = Low` |
| `attr:critical` | `*/15 * * * * *` | `Priority = Critical`, method parameters resolved from DI |
| `attr:business-hours` | `0,30 8-18/2 * * MON-FRI` | lists, ranges, steps, day names |
| `attr:daily-0330` | `30 3 * * ?` | `?` alias, and the effect of `CRONNER_TIMEZONE` |
| `attr:manual` | *(none)* | manual/one-shot task — only runs via `POST /tasks/attr:manual/run` |
| `enqueue:notify` | *(none)* | enqueue-only `[CronnerTask]` — run one-off instances with a typed payload via `POST /enqueue/notify` |
| `CronTestApp.Jobs.AttributeJobs.AutoNamedTask` | `*/30 * * * * *` | id derived from `Type.Method` when none is given |
| `attr:static` | `*/45 * * * * *` | a **static** task method, all parameters from DI |
| `conc:drop` | `*/5 * * * * *` | `DropAndForget` — a 12s run swallows the ticks it overlaps |
| `conc:queue` | `*/5 * * * * *` | `Queue` — an 8s run, missed ticks run back-to-back |
| `conc:parallel` | `*/5 * * * * *` | `Concurrent` — runs overlap (watch `inFlight`) |
| `progress:import` | `*/30 * * * * *` | `ICronnerJobContext` by constructor injection: total + two named scopes, each report carrying a custom payload (`note` in `/progress`); sets `SetExecutionData` + run-state bag |
| `lock:keepalive` | *(none)* | a 35s run under a 20s `LockTtl` — the keepalive renews the claim (`OnLockAcquire`, several `OnKeepAlive`, `OnLockRelease`) |
| `lock:stealable` | *(none)* | steal its claim with `steal-lock` → the next renewal fails, the run is cancelled, `OnLockLost` fires |
| `cancel:long-runner` | *(none)* | honours its token: run it, then cancel it, and `OnCancel` fires |
| `marker:filtered` | `*/20 * * * * *` | implements `IScheduledJob`, the marker used by `filtered` discovery |
| `external:cleanup` | `*/2 * * * *` | lives in the **`CronTestApp.ExternalJobs` assembly** (`AutoDiscoverFromAssembly`) |
| `external:marker` | `*/20 * * * * *` | external assembly **and** marker interface |

### `Sched<T>(...)` lambda tasks

| Id | Cron | Demonstrates |
| --- | --- | --- |
| `lambda:hello` | `*/10 * * * * *` | literal arguments + `WithId` / `WithPrio` / `WithConcurrency` |
| `lambda:service` | `*/25 * * * * *` | `HasParam<IGreeter>()`, `HasParam<ScopeMarker>()` (a new scope id each run) |
| `lambda:tenant` | `*/35 * * * * *` | the factory overload `HasParam<Tenant>(sp => …)` |
| `lambda:import` | `*/15 * * * * *` | an **async** target — the returned `Task` is awaited |
| `progress:reindex` | `*/45 * * * * *` | `HasParam<ICronnerJobContext>()` + **every per-schedule hook style**; sends a per-step progress payload the delegate hook logs |
| `CronTestApp.Jobs.LambdaJobs.SayHello` | `0 * * * *` | the short `Sched(expr, "cron")` overload |
| `lambda:manual` | *(none)* | a lambda task with no cron → manual only |

### Hooks — all twelve events, all four registration styles

| Style | Where | What it covers |
| --- | --- | --- |
| `ICronnerTaskHook` class | `Hooks/LoggingHook.cs`, via `AddHook<LoggingHook>()` | **all twelve events** for every task; owns `/metrics` and `/progress` |
| Global delegates | `UseTestAppCronner` | `OnStart` / `OnSuccess` / `OnFail` / `OnCancel` / `OnLockLost` — the `[delegate hook] …` lines |
| Per-schedule | the `progress:reindex` registration | `WithHook<ScheduleScopedHook>()` + a delegate `OnTotalProgressChange`, firing for that task only |
| Method-call expression | the same registration | `OnStart<AuditHook>(h => h.Record(h.HasParam<JobActivityLog>(), …))` and its async twin |

In `dedicated` DI mode the hook classes are registered in the dedicated container instead, since hooks
resolve from whichever provider runs the jobs. **Lock and progress hooks always run in their own fresh
scope**; **terminal hooks** (`OnStart`/`OnSuccess`/`OnFail`/`OnCancel`) run in the **job's** scope by default
(`CRONNER_HOOK_SCOPE=shared`) so they can read the job's scoped state — set `CRONNER_HOOK_SCOPE=isolated` to
flip the default, or pass a scope to `AddHook`/`WithHook` to pin a single hook. `LoggingHook`'s progress
methods use `context.HasParam<T>()` to prove they resolve from the hook's own scope. The global delegate
hooks show the rest: `OnSuccess` reads the run-state bag (`context.Get<ProgressJobs.ImportSummary>()`) and
`OnFail` logs `context.WillRetry`.

---

## Things worth knowing while testing

* **Persistent stores remember everything.** Redis / Postgres / the JSON file keep task state across
  restarts — a task you cancelled stays `Cancelled`, and tasks from a previous run stay in the store.
  Reset with `docker compose down -v` (or delete the JSON file).
* **Switching discovery modes leaves orphans behind** in a persistent store: tasks that are no longer
  discovered get claimed once and logged as *"No descriptor registered for task …; unscheduling it"*, then
  marked `Failed`. That is the expected library behaviour, not a bug in the harness.
* **Cron is validated at build time.** The bundled analyzer is wired in as an analyzer reference, so a
  broken `[CronnerTask]` cron is a build error (`DC0001`), and duplicate explicit ids are `DC0002`.
* **Async lambda tasks bind to the `Expression<Func<TJob, Task>>` overload** (`lambda:import`), so they
  compile warning-free and the scheduler awaits the returned `Task` — the run only counts as finished when
  the task completes (visible as a ~2s duration in `/activity`).
* **`CRONNER_REDIS_KEY_PREFIX` must not contain a `cache:` segment.** The cache provider appends its own,
  so `cronner:` yields `cronner:cache:<id>`.
* **`CRONNER_LOCK_TTL_SECONDS` is deliberately 20s** so the keepalive renews every 10s and the `lock:*`
  jobs produce visible `OnKeepAlive` events. Raise it to a production-ish 60s and those runs finish before
  a single renewal happens.
* **The keepalive cadence is independent of hook duration.** `OnKeepAlive` hooks are dispatched detached
  from the renewal loop, and each cycle schedules the next renewal at `interval − elapsed`, so renewals land
  every `LockTtl`/2 no matter what the hook does. Verify it yourself:

  ```powershell
  # 20s TTL → renew every 10s, with a keepalive hook that blocks for 25s
  $env:CRONNER_LOCK_TTL_SECONDS=20; $env:CRONNER_SLOW_KEEPALIVE_MS=25000
  # POST /tasks/lock:keepalive/run, then GET /locks
  #   "gapsSeconds": [10, 10], "onCadence": true   ← hook duration does not leak into the cadence
  ```

  A corollary: because those hooks are detached and carry the heartbeat's cancellation token, a keepalive
  hook still running when the job finishes is simply cancelled. Fine for a notification, not a place to do
  work that must complete.
* **`context.Job` in a hook is the snapshot taken when the claim was made.** On `OnKeepAlive` it still
  shows the *original* `LockedUntilUtc` (the renewed value is in the store), and on `OnLockLost` it still
  shows the *old* `LockOwner`, not the thief. The activity messages say so explicitly rather than pretend
  otherwise.
* **The custom store implements the whole new contract** — `RenewLockAsync` plus the optional
  `OnStartAsync`/`OnCloseAsync` per-run session, which logs `[store] session … opened/committed` lines so
  you can see each run bracketed.
* **No task fails on purpose any more.** The `fail:*` jobs were removed once error handling was proven;
  `OnFail` is still wired, so an unexpected exception still shows up in `/metrics` and `/activity`.

---

## Layout

```
.env.example                      copy to .env; every knob, read by compose and by the app
docker-compose.yml                app + redis 8.10 + postgres 18.6 (build context = repo root)
docker-compose.override.yml       dev-only environment/log levels
CronTestApp/
  Program.cs                      .env → options → services → schema → schedule → endpoints
  Configuration/
    DotEnvFile.cs                 minimal .env reader for non-compose runs
    TestAppOptions.cs             parses and validates every CRONNER_* variable
    CronnerSetup.cs               ALL the DotnetCronner wiring (store, cache, discovery, DI, hooks, Sched)
    StoreBootstrap.cs             EnsureCreated with retries while Postgres boots
  Data/AppDbContext.cs            own DbContext implementing ICronnerDbContext
  Stores/JsonFileCronnerStore.cs  hand-written ICronnerStore incl. RenewLockAsync + per-run session
  Jobs/                           all attribute + lambda job classes (lock, progress, concurrency, …)
  Hooks/LoggingHook.cs            ICronnerTaskHook via DI — all twelve events
  Hooks/AuditHook.cs              targets for the expression + per-schedule hook styles
  Endpoints/CronnerEndpoints.cs   the ICronnerClient management surface
  CronTestApp.http                ready-made requests
CronTestApp.ExternalJobs/         second assembly: IScheduledJob marker + external attribute tasks
```
