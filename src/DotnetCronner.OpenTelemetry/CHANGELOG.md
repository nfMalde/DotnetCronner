# Changelog — DotnetCronner.OpenTelemetry

All notable changes to the **DotnetCronner.OpenTelemetry** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-10-18

First release, alongside the suite at 0.2.0 (depends on `DotnetCronner >= 0.2.0`).

### Added
- `AddCronnerInstrumentation()` on `MeterProviderBuilder` and on `TracerProviderBuilder` — registers the
  scheduler's metrics and spans in one call:

  ```csharp
  services.AddOpenTelemetry()
      .WithMetrics(m => m.AddCronnerInstrumentation())
      .WithTracing(t => t.AddCronnerInstrumentation());
  ```

This package is **convenience, not a requirement**. The scheduler publishes on a base-class-library `Meter`
and `ActivitySource`, so `AddMeter(CronnerDiagnostics.MeterName)` and
`AddSource(CronnerDiagnostics.ActivitySourceName)` do exactly the same thing with no extra reference. It
depends on `OpenTelemetry.Api` only — never the SDK — so it cannot pull a second provider in behind your
application's own. See [docs/observability.md](../../docs/observability.md).
