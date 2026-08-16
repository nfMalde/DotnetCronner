# Changelog — DotnetCronner.Stores.EntityFrameworkCore

All notable changes to the **DotnetCronner.Stores.EntityFrameworkCore** package are documented here. The
format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
