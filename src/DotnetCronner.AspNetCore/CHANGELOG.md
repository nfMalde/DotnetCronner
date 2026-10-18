# Changelog — DotnetCronner.AspNetCore

All notable changes to the **DotnetCronner.AspNetCore** package are documented here. The format is based
on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-10-18

First release, alongside the suite at 0.2.0 (depends on `DotnetCronner >= 0.2.0` and
`DotnetCronner.Abstractions >= 0.2.0`).

### Added
- `services.AddHealthChecks().AddCronner()` — reports whether the scheduler started, is still polling for
  due work, and can reach its store. Each probe can be turned off via `CronnerHealthCheckOptions`.
- Optional stalled-execution detection: set `StalledRunThreshold` and a single run exceeding it reports
  `Degraded`. Off by default, because only the application knows what "too long" means for its jobs.
- `CronnerHealthCheck` and `CronnerHealthCheckOptions` are public, so the check can also be registered or
  wrapped by hand.

### Notes
- **A failing job never makes the application unhealthy.** A task that throws is the application's to
  handle — via `OnFail`, the `cronner.jobs.failed` metric, or execution history — not a reason for an
  orchestrator to restart a process that is otherwise working. The check reports on the *scheduler*.
- `Unhealthy` means the scheduler is broken and nothing will run until someone intervenes; `Degraded` means
  work is still moving, just not well. Pass `failureStatus: HealthStatus.Degraded` when the scheduler is not
  critical to the application. See [docs/observability.md](../../docs/observability.md).
