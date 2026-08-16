# Changelog — DotnetCronner.Stores.Redis

All notable changes to the **DotnetCronner.Stores.Redis** package are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.1] - 2026-08-16

### Added
- Initial release: `RedisCronnerStore` (crash-safe task claiming via per-job lock keys). Note:
  multi-instance / multi-node operation is not officially supported yet (planned).
- `RedisCronnerCacheProvider` second-level cache.
- Fluent `UseRedisAsStore(...)` and `UseRedisCacheProvider(...)` builder extensions.
