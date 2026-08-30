# Changelog — DotnetCronner

All notable changes to the **DotnetCronner** (core) package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.8] - 2026-08-28

Execution history & abstraction refinement (roadmap 0.0.8): the execution-history model is now explicit and
documented — see [docs/execution-history.md](../../docs/execution-history.md).

### Deprecated
- `ICronnerJobContext.SetExecutionData` / `TryGetExecutionData<T>` and the `CronnerTaskContext` equivalents.
  Execution history records an execution, not application data — keep app data/logs in your own store,
  correlated by `ctx.ExecutionId`, rather than on the execution record. Still functional; to be removed in a
  future release. The `DotnetCronner.Sample.WebApi` sample shows the replacement pattern (a `RunSummaryStore`
  behind `GET /runs`, keyed by `ExecutionId`).

## [0.0.7] - 2026-08-19

The "trust the lock" release: the cross-process single-run guarantee was reviewed, the holes that review
found are closed (see *Fixed*), and the guarantee is now **exercised by contended tests against real
PostgreSQL, SQL Server and Redis on every CI build** instead of being asserted in the README — plus the
execution id and the read-back the consumers' run-tracking needed to retire. Tests demonstrate the
scenarios they cover, not the absence of others; the known residual limits (a job that ignores its
cancellation token beyond the abandon margin, node clock skew for the time-compared stores, custom stores
that don't meet the contract) are documented under *Execution semantics* in the README.

### Added
- `CronnerTaskContext.ExecutionId` and `ICronnerJobContext.ExecutionId` — the id of the run in flight (the
  `CronnerJobExecution.Id` of its history row), visible to the job body and to every hook of the run from
  `OnLockAcquire` through the terminal event. Generated for every run even when history is off. A retry is a
  new execution with a new id.
- `CronnerTaskContext.TryGetExecutionData<T>(out T)` and `ICronnerJobContext.TryGetExecutionData<T>(out T)`
  — read back what the job or an earlier hook of the same run placed in the execution-data slot, so a later
  hook can augment the record instead of keeping its own copy.
- `CronnerTaskContext.LockTtl` / `CronnerTaskContext.KeepAliveInterval` — the effective values in force, so a
  consumer that tracks heartbeats can derive its staleness threshold instead of hardcoding one.
- **The lease rule.** A keepalive renewal the store cannot answer (throws, or does not answer in time) is no
  longer treated as "still held forever": the scheduler keeps the run alive while the last confirmed expiry
  is ahead, retries at a tighter cadence (interval/4, ≥ 250 ms) with a per-attempt deadline, and abandons the
  run — cancels it, fires `OnLockLost` — as soon as the next retry could not land before the lease lapses,
  i.e. always *before* another instance can claim the task. A renewal answered `false` stays an immediate
  loss. The scheduler also confirms a claim (one renewal) right before it starts a run and skips a task the
  store no longer confirms as its own.
- **Orphaned history rows are closed.** Right before recording a new run of a non-concurrent task the
  scheduler finalizes that task's still-`Running` rows as `Failed` with `CronnerExecutionErrors.Orphaned`
  (`ICronnerStore.FinalizeOrphanedExecutionsAsync`); a run abandoned for a lost/unconfirmable lock finalizes
  its own row as `Cancelled` with `CronnerExecutionErrors.LockLost`.
- Startup warnings when `KeepAliveInterval` is above `LockTtl`/2 (one failed renewal may force an abandon)
  and an error when it is at or above `LockTtl`.
- Shared, reusable lock contract tests (`StoreLockContractTests`, `SchedulerExclusivityTests`,
  `tests/DotnetCronner.Tests/Shared`) that any custom store can be pointed at, and a new
  `tests/DotnetCronner.IntegrationTests` project running them against PostgreSQL, SQL Server (READ COMMITTED
  with and without snapshot) and Redis via Testcontainers (skipped without Docker, required in CI).

### Changed
- Lock release goes through the new owner-conditional `ICronnerStore.ReleaseLockAsync` (after the outcome
  is persisted) instead of an `UpsertAsync` with cleared lock fields; the engine never writes lock fields
  through `UpsertAsync` any more.
- `OnLockLost` now fires in two situations, both after the run was cancelled: a renewal was refused
  (reclaimed elsewhere), or the lease could not be confirmed before it lapsed. A renewal refused because the
  task is `Cancelled` (a cancel from another instance) is classified as a cancel — `OnCancel`, not
  `OnLockLost`.
- `ICronnerClient.CancelTaskAsync` no longer clears lock fields; a task running on another instance stops at
  its next keepalive because shipped stores refuse to renew a cancelled task. The scheduler honours a
  `Cancelled` state set in the store during a run when it writes the outcome.
- The in-memory store preserves an existing row's lock fields on `UpsertAsync`, refuses to renew a
  `Cancelled` task, and implements `ReleaseLockAsync` / `FinalizeOrphanedExecutionsAsync`.
- `CachedCronnerStore` forwards the new store members (a default implementation would have run against the
  decorator and never released a lock) and invalidates instead of caching the written object on `UpsertAsync`.
- `ICronnerJobContext` gained members (`ExecutionId`, `TryGetExecutionData`) — breaking only for external
  implementors of that interface; `CronnerHookDispatcher` now takes `IOptions<CronnerOptions>`.

### Fixed
- **Same-process double run:** the poll loop claimed up to `MaxConcurrentTasks` due tasks per tick regardless
  of how many workers were busy. A claimed task waiting in the dispatch queue has no heartbeat, so one that
  waited longer than `LockTtl` lost its lease and could be claimed — and run — a second time, even by the
  same instance. The poll loop now claims only as many tasks as there are free workers, and a non-concurrent
  task already running in the process is never started twice.
- **Stale snapshot could free someone else's lock:** `TriggerNowAsync`, seeding at startup and
  `CancelTaskAsync` read a job and wrote it back in full; a claim taken by another instance in between was
  cleared (or its expiry regressed) by that write, and a third claim could run the task concurrently. Lock
  fields are now written only by the lock primitives (see the Abstractions changelog); the in-memory store
  enforces it, the EF Core and Redis stores do in their own releases.
- A hanging `RenewLockAsync` (a store call that never returns and ignores its token) no longer lets the
  lease lapse silently under a running task — every renewal attempt has its own deadline.

## [0.0.6] - 2026-08-18

### Added
- `CronnerStoreLifetime` (`Singleton` | `Scoped`) declares how often the configured store is built, passed
  to `UseStore<TStore>(lifetime)` and `CronnerStoreHolder.ConfigureStore(...)`. `Singleton` (the default,
  and the previous behavior) builds once and reuses the instance — correct for a store that is stateless or
  owns its own state, such as the in-memory store, Redis, or anything holding a factory. `Scoped` builds the
  store per scheduler operation from that operation's DI scope — correct for a store holding a `DbContext`,
  an ORM session, or an open connection.
- `CronnerStoreAccessor`: opens a DI scope per store operation and resolves the store from it, so the
  scheduler and client — both singletons — never capture a scoped store for the process lifetime.

### Fixed
- A store holding a scoped, non-concurrent resource was shared across overlapping scheduler operations.
  `ICronnerStore` was registered as a singleton built from the **root** provider, so a store depending on a
  `DbContext` or ORM session captured one instance for the application's lifetime; the scheduler polls while
  jobs run, so its methods overlap and issue concurrent commands on one connection. That surfaces as
  `NpgsqlOperationInProgressException` ("a command is already in progress") or EF Core's "a second operation
  was started on this context instance", and only once enough tasks are registered for the seeding fan-out to
  overlap — so it hides in small samples and appears under load. Stores declared `Scoped` now get a fresh
  instance, resolved from the operation's own scope, per call.
  The built-in in-memory, Redis and EF Core stores are unaffected — they are already safe for concurrent use
  and remain `Singleton`.

### Changed
- `ICronnerStore` is registered as **scoped** rather than singleton, so an injected store follows the ambient
  scope and honours the configured lifetime. A `Singleton` store still resolves to one shared instance.
- `CronnerStoreHolder.Build(IServiceProvider)` is obsolete in favour of `Resolve(IServiceProvider)`; it could
  not honour a scoped lifetime. `Build` still compiles and behaves as `Singleton`.
- **Breaking:** `CronnerClient` takes a `CronnerStoreAccessor` instead of an `ICronnerStore`. Affects only
  code constructing `CronnerClient` directly; resolving `ICronnerClient` from DI is unchanged.
- Configuring a second store now names the store already in effect:
  `"A custom store UseStore<X>() is already used. You can only use one store. 'UseRedisAsStore()' would
  replace it."` — when someone hits this, the useful information is which store they are already on.

## [0.0.5] - 2026-08-18

### Added
- Execution history (opt-in via `WithExecutionHistory(keepPerTask)`; `0`, the default, is off): each run
  records a `Running` history entry when it starts and finalizes it to `Succeeded` / `Failed` / `Cancelled`
  when it ends, correlated so it works even for concurrent runs of the same task. Older entries are pruned to
  the per-task cap after each run. Read them back with `ICronnerClient.GetExecutionsAsync`. The built-in
  in-memory and cached stores persist history; a `Running` entry left behind marks a crashed/stalled run.
  Each entry also carries the owning scheduler instance and an optional consumer JSON blob set via
  `ctx.SetExecutionData(...)`.
- Per-run **state bag**: `ctx.Set<T>()` / `ctx.Get<T>()` / `ctx.TryGet<T>()` on `ICronnerJobContext` (job)
  and `CronnerTaskContext` (hooks). A value the job stashes is visible to that run's `OnKeepAlive` and
  terminal hooks — including `OnFail` — regardless of hook scope. Keyed per run, so concurrent runs never
  share. (`OnStart` fires before the body, so it cannot see job-set values.)
- **Per-hook scope** for terminal hooks: pass a `CronnerHookScope` to `AddHook` / `WithHook`
  (`AddHook<AuditHook>(CronnerHookScope.Isolated)`) so an individual hook runs `Isolated` (its own fresh
  scope, never contending with a single-session unit of work the job holds) while others stay `Shared`.
  `CronnerOptions.HookScope` (set via `Configure`) sets the default for hooks that don't specify one; it
  defaults to `Shared` (the 0.0.4 behavior — terminal hooks run in the job's scope). Lock and progress hooks
  always run isolated regardless.
- `CronnerOptions.OnInvalidSchedule`: `MarkFailed` (default — mark a never-firing task Failed and keep
  scheduling the rest) or `Throw` (fail host startup so the app refuses to boot until the cron is fixed).
- `CronnerTaskContext.WillRetry`: on `OnFail`, whether the scheduler will retry (there are attempts left) —
  so a failure hook can hold off alerting until the final attempt.
- Per-report **progress payload**: `ctx.Progress(value, payload)` / `ProgressAsync(...)` and the scope
  equivalents (plus `OpenProgressScope(category, payload)`) attach any object to a progress report, delivered
  to the total/scope progress hook as `CronnerTaskContext.ProgressPayload` — for per-report detail a scope's
  category can't carry (current step, item id, partial result). Progress hooks also read the run-state bag.

## [0.0.4] - 2026-08-17

### Fixed
- Republished to correct the `DotnetCronner.Abstractions` dependency floor to `>= 0.0.4`. The 0.0.3 package
  shipped referencing `>= 0.0.2` because of a release-order race (core was released before the
  `abstractions-v0.0.3` tag existed), which let NuGet resolve an incompatible Abstractions 0.0.2. All four
  packages are re-released together at 0.0.4 with matching floors. **0.0.3 is superseded (unlisted) — use
  0.0.4.** No functional code changes from 0.0.3.

## [0.0.3] - 2026-08-17

### Added
- Enqueue one-off jobs with typed payloads (`EnqueueAsync<TPayload>`): the payload is JSON-serialized as its
  declared type — cycle-safe (`ReferenceHandler.IgnoreCycles` + bounded depth) and proxy-safe — and
  delivered to the matching method parameter; the instance runs once and then completes.
- Built-in one-off retention: `WithOneOffRetention(keepNewest)` prunes older finished instances per
  definition after each completes.
- `WithKeepAliveInterval(TimeSpan)` to pin the keepalive / `OnKeepAlive` cadence independently of `LockTtl`.
- `WithDescription(...)` on the schedule options; `GetRegisteredTasks()` and `TriggerNowAsync()` on the client.
- Total progress is now persisted to the store via a targeted update that never races the keepalive.

### Changed
- **Terminal lifecycle hooks (`OnStart`/`OnSuccess`/`OnFail`/`OnCancel`) now run in the job's execution
  scope**, so a hook's `ctx.HasParam<T>()` resolves the same scoped instances the job used (e.g. read back a
  summary the job wrote). Lock and progress hooks keep their own scope. This is a documented guarantee.

### Fixed
- A cron expression that parses but never produces a next occurrence (e.g. 31 February) is now logged as an
  error and the task is marked `Failed` instead of silently never running. Seeding is per-task and isolated,
  so one such task can never prevent the others from being scheduled.

## [0.0.1] - 2026-08-16

### Added
- Initial release: the core scheduler engine and in-memory store.
- Cron scheduling via a zero-dependency parser (5-field, or 6-field with seconds).
- Two registration styles: `[CronnerTask]` attribute scanning and fluent `Sched<T>(...)` lambdas.
- Attribute-discovery controls: `DisableAutoDiscovery()`, `AutoDiscoverFromAssembly(...)`,
  `AutoDiscoverFromType(...)`.
- `HasParam<T>()` markers for run-time parameter resolution, including the
  `HasParam<T>(sp => ...)` factory overload to resolve values yourself from the scope provider.
- `Sched` overloads for `Expression<Func<TJob, Task>>` so scheduling async jobs is warning-free (no CS4014
  at the call site).
- Per-task priority and concurrency modes (`DropAndForget`, `Queue`, `Concurrent`); single-run-per-id by default.
- Each job run is bracketed by the store's `OnStartAsync` (before any store action) and `OnCloseAsync`
  (always, even on failure), so a store can open and close a per-run session.
- Execution-lock keepalive: a running task renews its claim on a fixed cadence (~`LockTtl`/2), so long
  jobs are never mistaken for a stalled worker and re-run. Renewals are independent of `OnKeepAlive` hook
  duration — the hook is fired without blocking the renewal, so a slow hook can't cost the lock. A task is
  only reclaimed after a worker stops renewing for longer than `LockTtl` (logged as a warning); a worker
  that loses its lock mid-run cancels its own execution so a task never runs twice at once.
- Lifecycle and lock hooks (`ICronnerTaskHook` + fluent `OnStart`/`OnSuccess`/`OnFail`/`OnCancel` and
  `OnLockAcquire`/`OnKeepAlive`/`OnLockRelease`/`OnLockLost`), registrable **globally or per schedule**,
  as a delegate, as a `HasParam` method-call expression, or as an `ICronnerTaskHook`. Each hook invocation
  runs in its own DI scope; `CronnerTaskContext` exposes `Services` and `HasParam<T>()`.
- Progress reporting: tasks pull `ICronnerJobContext` from DI to report total progress
  (`Progress`/`ProgressAsync`) and per-subtask progress via `OpenProgressScope(...)`. Progress raises the
  `OnTotalProgressChange`/`OnProgressScopeOpened`/`OnScopeProgress`/`OnProgressScopeClosed` hooks.
- Configurable DI scope handling with `WithDedicatedDI(...)`.
- Optional second-level cache in front of any store via `UseSecondLevelCache(...)` (`CachedCronnerStore`).
- In-process management API `ICronnerClient`.
- Bundled Roslyn analyzer: `DC0001` (invalid cron) and `DC0002` (duplicate task id), plus fail-fast
  cron validation at registration.
