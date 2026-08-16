# Changelog — DotnetCronner

All notable changes to the **DotnetCronner** (core) package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
