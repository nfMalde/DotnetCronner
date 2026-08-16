# Changelog — DotnetCronner.Abstractions

All notable changes to the **DotnetCronner.Abstractions** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.1] - 2026-08-16

### Added
- Initial release: `ICronnerStore` (including atomic `AcquireDueAsync`, `RenewLockAsync`, and optional
  per-run `OnStartAsync`/`OnCloseAsync` lifecycle for opening/closing a per-run session),
  `ICronnerCacheProvider`, `ICronnerClient`.
- Models: `CronnerJob`, `CronnerTaskState`, `CronnerTaskPriority`, `CronnerConcurrencyMode`, and the
  reusable `CronnerJobEntity` persistence shape (mutable, `virtual` properties, `ToDomain`/`From`/`Apply`).
- The `[CronnerTask]` attribute.
