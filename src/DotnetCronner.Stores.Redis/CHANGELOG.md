# Changelog — DotnetCronner.Stores.Redis

All notable changes to the **DotnetCronner.Stores.Redis** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
