# Changelog — DotnetCronner.Abstractions

All notable changes to the **DotnetCronner.Abstractions** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.8] - 2026-08-28

Execution history & abstraction refinement (roadmap 0.0.8): the execution-history model is now explicit and
documented — see [docs/execution-history.md](../../docs/execution-history.md).

### Added
- `CronnerJobExecution.Duration` — computed `FinishedAt - StartedAt` (`null` while running), so a run's
  elapsed time is available without a stored column.

### Deprecated
- `CronnerJobExecution.Data` and `JobExecutionEntity.Data`. Execution history records an execution, not
  application data — keep app data/logs in your own store, correlated by the execution id (`ctx.ExecutionId`).
  Still mapped for now; to be removed in a future release.

### Documentation
- `JobExecutionEntity` now documents why it carries no primary key and no job foreign key: the scheduler
  correlates runs by the string execution id, so the surrogate key's *type* is the store's/database's choice
  (int / long / Guid / string) — the abstraction does not impose one. The base holds only the run's intrinsic
  fields; each store adds its own key and job link.

## [0.0.7] - 2026-08-19

### Added
- `ICronnerStore.ReleaseLockAsync(id, owner)` — owner-conditional release of a task's execution lock; returns
  whether the caller held it. Default-implemented (read → owner check → clear → `UpsertAsync`) so existing
  custom stores keep compiling; a store whose `UpsertAsync` preserves lock fields (the recommended contract)
  must override it with an atomic statement.
- `ICronnerStore.FinalizeOrphanedExecutionsAsync(jobId, finishedAt, error)` — sets every still-`Running`
  execution record of a job to `Failed` with the given finish time and error and returns the count. Default
  no-op. The scheduler calls it right before recording a new run of a non-concurrent task, so history rows
  left behind by a crashed owner are closed.
- `CronnerExecutionErrors` — the well-known `CronnerJobExecution.Error` texts the scheduler writes itself:
  `Orphaned` (a `Running` row whose owner never finished it) and `LockLost` (a run abandoned because its lock
  was lost or could not be confirmed).

### Changed
- The store contract is now spelled out in the interface's XML docs (and `docs/custom-store.md`):
  `LockOwner`/`LockedUntilUtc` are owned exclusively by `AcquireDueAsync` / `RenewLockAsync` /
  `ReleaseLockAsync`; `UpsertAsync` must **preserve** an existing row's lock fields; `AcquireDueAsync` must
  re-assert eligibility atomically with the claim; `RenewLockAsync` returns `false` only when the claim is
  definitively not the caller's (reclaimed, released, gone, or the task is `Cancelled`) and **throws** when it
  cannot tell — the scheduler owns the unconfirmed-lease policy. Source-compatible: no signature changed.

## [0.0.6] - 2026-08-18

### Added
- `CronnerStoreLifetime` (`Singleton` | `Scoped`): declares how often DotnetCronner builds the configured
  `ICronnerStore`. Deliberately explicit rather than inferred from a DI registration — the store is consumed
  by a singleton scheduler, so a DI lifetime alone cannot express it, and a scoped registration resolved from
  the root provider silently becomes captive.

## [0.0.5] - 2026-08-18

### Added
- Execution history: `CronnerJobExecution` (one recorded run — start/finish, `JobExecutionStatus`, attempt,
  error, plus `Owner` = which scheduler instance ran it and `Data` = optional consumer JSON), the abstract
  `JobExecutionEntity` persistence base (each store adds its own key and job link), and the `ICronnerStore`
  methods `RecordExecutionStartedAsync` / `RecordExecutionFinishedAsync` / `GetExecutionsAsync` /
  `PruneExecutionsAsync` (all default no-op / empty, so existing custom stores are source-compatible).
- `ICronnerClient.GetExecutionsAsync(taskId, limit)` to read a task's recent runs, newest first.

## [0.0.4] - 2026-08-17

### Changed
- Version alignment only: re-released at 0.0.4 so the whole suite shares one version and the dependent
  packages (core, EF Core, Redis) can pin `DotnetCronner.Abstractions >= 0.0.4` after a 0.0.3 release-order
  race left their floors pointing at an incompatible Abstractions 0.0.2. **No API or behavior changes from
  0.0.3.** 0.0.3 remains fully compatible; it is superseded (unlisted) only to keep the suite versions in step.

## [0.0.3] - 2026-08-17

### Added
- One-off / enqueued jobs: `ICronnerClient.EnqueueAsync<TPayload>(...)`, plus `CronnerJobKind` and the
  `CronnerJob`/`CronnerJobEntity` fields `Kind`, `DefinitionId`, `Payload`, `PayloadType` — a registered task
  can be enqueued with a typed payload delivered as a method parameter, run once, and retained/pruned.
- `ICronnerClient.GetRegisteredTasks()` returning `CronnerRegisteredTask` — every registered definition
  (including never-run / manual ones), for admin listings.
- `ICronnerClient.TriggerNowAsync(id)` — the unambiguous "run now" (`ScheduleTaskAsync` is now an alias).
- `CronnerTaskAttribute.Description`, surfaced on `CronnerRegisteredTask`.
- `CronnerJob.Progress` / `CronnerJobEntity.Progress` and `ICronnerStore.UpdateProgressAsync(...)` (total
  progress is persisted); `ICronnerStore.PruneCompletedOneOffsAsync(...)` for one-off retention. Both are
  default no-ops so existing custom stores keep compiling.
- `CronnerJobEntity.ToDomain()` / `Apply(job)` are now `virtual` (subclasses can extend the mapping).

### Changed
- **Breaking:** `CronnerJobEntity.Id` renamed to `CronnerJobEntity.TaskId` (still a `string` primary key),
  so it no longer collides with an `int`/`long` surrogate-key convention on consumer entities, base classes,
  or automappers. The domain `CronnerJob.Id` is unchanged.
- `ICronnerClient.CancelTaskAsync` clarified: cancels a currently running execution (via its token) or
  unschedules a pending one.

## [0.0.1] - 2026-08-16

### Added
- Initial release: `ICronnerStore` (including atomic `AcquireDueAsync`, `RenewLockAsync`, and optional
  per-run `OnStartAsync`/`OnCloseAsync` lifecycle for opening/closing a per-run session),
  `ICronnerCacheProvider`, `ICronnerClient`.
- Models: `CronnerJob`, `CronnerTaskState`, `CronnerTaskPriority`, `CronnerConcurrencyMode`, and the
  reusable `CronnerJobEntity` persistence shape (mutable, `virtual` properties, `ToDomain`/`From`/`Apply`).
- The `[CronnerTask]` attribute.
