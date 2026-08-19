# Changelog — DotnetCronner.Stores.EntityFrameworkCore

All notable changes to the **DotnetCronner.Stores.EntityFrameworkCore** package are documented here. The
format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.7] - 2026-08-19

**No migration required** — no schema change in this release.

### Added
- `ReleaseLockAsync`: one owner-conditional `ExecuteUpdate` (`WHERE TaskId = @id AND LockOwner = @owner`)
  that clears the lock columns — a former owner cannot free a lock someone else holds now.
- `FinalizeOrphanedExecutionsAsync`: one targeted `ExecuteUpdate` setting the job's still-`Running` history
  rows to `Failed` with the given finish time and error (the existing `(TaskId, StartedAt)` index covers it).
- **Validated multi-instance claiming.** The shared lock contract and two-scheduler exclusivity suites run
  against real **PostgreSQL** and **SQL Server** (READ COMMITTED, with and without `READ_COMMITTED_SNAPSHOT`)
  in `tests/DotnetCronner.IntegrationTests` on every CI build: eight instances racing for forty due tasks
  hand each out exactly once, renew/release are owner-conditional, a stale `Upsert` never clears a foreign
  lock, two schedulers on one database never run a task concurrently, a scheduler that loses its database
  mid-run is stopped before the survivor reclaims, and a cancel from the other instance stops the run.

### Changed
- `UpsertAsync` **preserves an existing row's `LockOwner`/`LockedUntilUtc`** (it re-applies the tracked
  entity's current values after mapping the incoming job; EF emits no `SET` for the unchanged columns). Lock
  columns are written only by `AcquireDueAsync` / `RenewLockAsync` / `ReleaseLockAsync`, so a caller writing
  a stale snapshot can never clear or shorten a claim another instance took in the meantime.
- `RenewLockAsync` additionally requires `State <> Cancelled`, so a cancel issued from another instance stops
  the running instance at its next keepalive.
- `AcquireDueAsync` re-reads candidates (bounded) when other instances won every row of the page it looked
  at, so an instance with free capacity is not starved by a head-of-line full of rows that were just taken;
  the claim itself is unchanged — a conditional `UPDATE` that re-asserts eligibility in the statement, which
  is race-safe at READ COMMITTED on PostgreSQL, SQL Server and MySQL without a transaction or concurrency
  token.

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
