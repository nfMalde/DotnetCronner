# Changelog — DotnetCronner

All notable changes to the **DotnetCronner** (core) package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
