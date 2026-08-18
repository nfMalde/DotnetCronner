# Changelog — DotnetCronner.Abstractions

All notable changes to the **DotnetCronner.Abstractions** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
