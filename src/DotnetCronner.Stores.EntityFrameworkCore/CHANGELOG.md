# Changelog — DotnetCronner.Stores.EntityFrameworkCore

All notable changes to the **DotnetCronner.Stores.EntityFrameworkCore** package are documented here. The
format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.6] - 2026-08-18

### Changed
- Version bump to build against the latest `DotnetCronner` / `DotnetCronner.Abstractions` API
  (`CronnerStoreLifetime`). No behavior change to the EF Core store itself.
- Declares `CronnerStoreLifetime.Singleton` explicitly when configuring the store. No behavior change:
  this store holds an `IDbContextFactory<TContext>` and creates a short-lived `DbContext` per call, so it
  is already safe for the concurrent operations the scheduler performs. Stated in code so the choice is
  visible rather than inherited from a default.

## [0.0.5] - 2026-08-18

### Added
- Execution-history persistence: a new `CronnerJobExecutions` table (entity `CronnerJobExecutionEntity`, a
  numeric identity key + `TaskId` foreign key to the jobs table with `ON DELETE CASCADE` + an indexed
  correlation id, plus `Owner` and `Data` columns) with `RecordExecutionStartedAsync` (insert),
  `RecordExecutionFinishedAsync` (targeted `ExecuteUpdate` matched on the correlation id), `GetExecutionsAsync`
  and `PruneExecutionsAsync`.

### ⚠️ Migration required
- This adds a new table and mapping. **After upgrading you must generate and apply a new EF Core migration**
  (`dotnet ef migrations add AddCronnerJobExecutions` then `database update`, or your normal migration flow).
  The scheduler only writes history when `WithExecutionHistory` is enabled, but the table must exist first.

## [0.0.4] - 2026-08-17

### Fixed
- Re-released together with the rest of the suite at 0.0.4 to pin correct cross-package dependency floors
  (`DotnetCronner >= 0.0.4`, `DotnetCronner.Abstractions >= 0.0.4`). The 0.0.3 packages carried floors that a
  release-order race left pointing at an incompatible Abstractions 0.0.2. **0.0.3 is superseded (unlisted) —
  use 0.0.4.** No functional code changes from 0.0.3.

## [0.0.3] - 2026-08-17

### Added
- Maps the one-off / payload / progress columns (`Kind`, `DefinitionId`, `Payload`, `PayloadType`,
  `Progress`) with a `(DefinitionId, State)` index, and implements `PruneCompletedOneOffsAsync` (retention)
  and `UpdateProgressAsync` (targeted single-column progress update).

### Changed
- **Breaking:** the `CronnerJobs` primary-key column follows `CronnerJobEntity.TaskId` (renamed from `Id`),
  so the key column is now `TaskId`. Freshly created databases are correct automatically; an existing table
  needs a column rename migration (plus the new columns above).

## [0.0.1] - 2026-08-16

### Added
- Initial release: `EfCronnerStore<TContext>`. Claims due tasks with a single conditional `UPDATE`
  (`ExecuteUpdate`) that re-asserts eligibility — correct on every provider, no concurrency-token column
  and no `RowVersion` (which was inert on PostgreSQL/SQLite and cost a `RETURNING` round-trip everywhere).
- Built-in `CronnerDbContext` that maps the tables itself (no custom context, no `ApplyCronnerModel` call
  to remember), plus a `UseEntityFrameworkStore(o => o.UseNpgsql(...))` overload that registers it.
- `ICronnerDbContext`, `ApplyCronnerModel(...)`, and the `UseEntityFrameworkStore<TContext>()` extension.
  The `CronnerJobEntity` type lives in `DotnetCronner.Abstractions` (with `virtual` properties for ORM proxies).
- Migration guide (`MIGRATIONS.md`) covering the built-in and own-context setups for both project layouts.
