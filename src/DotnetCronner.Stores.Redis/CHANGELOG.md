# Changelog — DotnetCronner.Stores.Redis

All notable changes to the **DotnetCronner.Stores.Redis** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.8] - 2026-08-28

Released with the suite at 0.0.8 (depends on `DotnetCronner.Abstractions >= 0.0.8` and
`DotnetCronner >= 0.0.8`). No store code changes.

### Changed
- Downstream of the abstraction refinement (roadmap 0.0.8): a persisted execution now serializes the new
  computed `CronnerJobExecution.Duration`, and the `Data` slot it may carry is **deprecated** (still read and
  written). No data-format break — older keys are read as before, and `Duration` is derived from the
  timestamps on read. See [docs/execution-history.md](../../docs/execution-history.md).

## [0.0.7] - 2026-08-19

No data-format change; existing keys are read as before.

### Added
- `ReleaseLockAsync`: a compare-and-delete Lua script on the lock key (deleted only while it still holds this
  owner), then the lock fields in the stored job are cleared for the informational view.
- `FinalizeOrphanedExecutionsAsync`: the job's still-`Running` history entries are rewritten as `Failed` with
  the given finish time and error.
- **Validated multi-instance claiming** against a real Redis in `tests/DotnetCronner.IntegrationTests` on
  every CI build (contended claims with small batches, renew/release ownership, stale upserts, two schedulers
  on one Redis, kill-and-reclaim, cross-instance cancel).

### Changed
- `UpsertAsync` **no longer deletes the lock key** when the incoming job has no owner, and keeps the stored
  job's lock fields on an update. The lock key is the source of truth for claiming and is now touched only
  by `AcquireDueAsync` (`SET NX PX`), `RenewLockAsync` (compare-and-pexpire) and `ReleaseLockAsync`
  (compare-and-delete), so a caller writing a stale snapshot can never free a claim another instance took.
- `RenewLockAsync` refuses to renew a `Cancelled` task (returns `false`), so a cancel issued from another
  instance stops the running instance at its next keepalive.
- `AcquireDueAsync` claims **highest priority first** within a page (it used to follow due-time order only),
  re-asserts eligibility from the fresh job after winning the lock key (a stale due-set member for a cancelled
  or rescheduled job is cleaned up instead of run), and pages through the due set (bounded).

### Fixed
- **Starvation across instances:** claimed/running jobs keep their old score in the due set until their run
  advances the schedule, and `AcquireDueAsync` looked at the first `max` members only — so a second instance
  with free capacity could see nothing but other instances' locked jobs and claim nothing. It now pages
  further.

## [0.0.6] - 2026-08-18

### Changed
- Version bump to build against the latest `DotnetCronner` / `DotnetCronner.Abstractions` API
  (`CronnerStoreLifetime`). No behavior change to the Redis store itself.
- Declares `CronnerStoreLifetime.Singleton` explicitly when configuring the store. No behavior change:
  `IConnectionMultiplexer` is thread-safe and intended to be shared, so one store instance serves
  concurrent scheduler operations safely. Stated in code so the choice is visible rather than inherited
  from a default.

## [0.0.5] - 2026-08-18

### Added
- Execution-history persistence: runs are stored in a per-job Redis hash (`<prefix>exec:<jobId>`, keyed by
  each run's correlation id), implementing `RecordExecutionStartedAsync` / `RecordExecutionFinishedAsync`
  (insert then finalize the same field), `GetExecutionsAsync` (newest first) and `PruneExecutionsAsync`.
  Removing a job deletes its history hash. No schema/migration step — enable it with `WithExecutionHistory`.

## [0.0.4] - 2026-08-17

### Fixed
- Re-released together with the rest of the suite at 0.0.4 to pin correct cross-package dependency floors
  (`DotnetCronner >= 0.0.4`, `DotnetCronner.Abstractions >= 0.0.4`). The 0.0.3 packages carried floors that a
  release-order race left pointing at an incompatible Abstractions 0.0.2. **0.0.3 is superseded (unlisted) —
  use 0.0.4.** No functional code changes from 0.0.3.

## [0.0.3] - 2026-08-17

### Added
- Implements `PruneCompletedOneOffsAsync` (one-off retention) and `UpdateProgressAsync`. The one-off
  payload / kind / progress fields serialize automatically with the job JSON — no format change needed.
- `UseRedisAsStore` / `UseRedisCacheProvider` overloads taking `Action<IServiceProvider, CronnerRedisOptions>`,
  so the connection and credentials can be configured from `IConfiguration` or any registered service.

### Docs
- Clarified that secured Redis (auth / ACL user + password, TLS) is supported via the connection string,
  `ConfigurationOptions`, a multiplexer factory, or a DI-registered `IConnectionMultiplexer`.

## [0.0.1] - 2026-08-16

### Added
- Initial release: `RedisCronnerStore` (crash-safe task claiming via per-job lock keys). Note:
  multi-instance / multi-node operation is not officially supported yet (planned).
- `RedisCronnerCacheProvider` second-level cache.
- Fluent `UseRedisAsStore(...)` and `UseRedisCacheProvider(...)` builder extensions.
