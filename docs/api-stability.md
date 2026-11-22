# API stability

DotnetCronner is still `0.x`, so nothing here is a SemVer promise yet. This page says which parts of the
public surface are expected to carry over to `1.0` unchanged, which may still move, and how the surface is
kept from changing by accident in the meantime.

## The three tiers

| Tier | Meaning before 1.0 | Meaning from 1.0 |
| --- | --- | --- |
| **Stable** | Changed only for a reason worth a changelog entry, with a migration note | Breaking changes require a new major version |
| **Provisional** | Public and supported, but the shape may still change in a minor release | Either promoted to Stable or removed before 1.0 |
| **Internal** | Not accessible; no compatibility of any kind | Same |

### Stable

What an application touches to schedule and manage work:

- Registration and configuration: `AddDotnetCronner`, `UseDotnetCronner`, `ICronnerBuilder`,
  `ICronnerScheduleOptions`, `ICronnerRetryPolicyBuilder`, `ICronnerCacheBuilder`, `CronnerOptions`,
  `[CronnerTask]`, `HasParam<T>()`.
- Inside a job and its hooks: `ICronnerJobContext`, `ICronnerProgressScope`, `ICronnerTaskHook`,
  `CronnerTaskContext`, `CronnerProgressInfo`.
- Management: `ICronnerClient`, `CronnerRegisteredTask`.
- The models and enums those APIs exchange: `CronnerJob`, `CronnerJobExecution`, `CronnerRetryPolicy`,
  `CronnerTaskState`, `CronnerTaskPriority`, `CronnerConcurrencyMode`, `CronnerJobKind`, `JobExecutionStatus`,
  `MisfirePolicy`, `CronnerRetryStrategy`, `CronnerHookScope`, `CronnerStoreLifetime`,
  `CronnerInvalidScheduleBehavior`, `CronnerExecutionErrors`.
- Exceptions: `CronnerTaskNotFoundException`, `DuplicateCronnerTaskException`, `CronFormatException`.
- The cron parser: `CronExpression`.
- Store selection: `UseStore<T>()`, `UseStore(factory)`, `UseRedisAsStore`, `UseEntityFrameworkStore`,
  `UseSecondLevelCache`, `UseRedisCacheProvider`, and their option types.
- Diagnostics names: `CronnerDiagnostics` (meter name, activity source name, tag keys, outcome values).
  Dashboards and alerts are built on these strings, so they are treated like API.

### Stable, with one caveat: the extension contracts

`ICronnerStore` and `ICronnerCacheProvider` are implemented by *your* code rather than called by it, which
changes what "compatible" means. The rule:

- Existing members keep their signature and their documented behavior.
- New members may be added in a minor release, **always with a default implementation**, so an existing
  store keeps compiling and keeps working. The default is the conservative behavior (a no-op, an empty
  result, or a read-then-write fallback), and the changelog says when a real override is worth writing.
- The behavior a store must show is defined by the shared contract suites, not by prose alone — see
  [Writing a custom store](custom-store.md).

The same applies to the persistence shapes meant for mapping or subclassing: `CronnerJobEntity`,
`JobExecutionEntity`, `CronnerJobExecutionEntity`, `ICronnerDbContext`, `CronnerDbContext`,
`ApplyCronnerModel`. A new column arrives as a new property with a default, and is called out as a
migration in the EF Core package's changelog.

### Provisional

Public and usable today, but recent enough that the shape may still change before 1.0:

- `CronnerSchedulerDiagnostics` — the live scheduler state behind the health check.
- The `DotnetCronner.AspNetCore` package (`AddCronner()` health check, `CronnerHealthCheckOptions`).
- The `DotnetCronner.OpenTelemetry` package (`AddCronnerInstrumentation()`).
- The concrete store classes as types: `InMemoryCronnerStore`, `RedisCronnerStore`, `EfCronnerStore<T>`,
  `CachedCronnerStore`, `RedisCronnerCacheProvider`. Selecting them through the builder is Stable;
  constructing them yourself is supported but their constructors may change.
- `CronnerJobExecution.Data` — already marked `[Obsolete]` and due to be removed.

### Internal

The scheduler engine itself: the hosted service, the task and hook registries, the store holder and
accessor, the execution tracker, the schedule calculator and the instrumentation. These types were public
up to `0.2.x` and are `internal` from `0.3.0`. Nothing in the documented API required them; if you were
resolving one from the container, the supported replacement is:

| Was using | Use instead |
| --- | --- |
| `ICronnerBuilder.StoreHolder.ConfigureStore(...)` | `ICronnerBuilder.UseStore(factory, lifetime, displayName)` |
| `CronnerRegistry` (to list tasks) | `ICronnerClient.GetRegisteredTasks()` |
| `CronnerClient` (the concrete class) | `ICronnerClient` |
| `CronnerExecutionTracker` (is it running?) | `ICronnerClient.GetTaskByIdAsync` / `CronnerSchedulerDiagnostics` |
| `CronnerHostedService` | Nothing — it is registered and started by `AddDotnetCronner` |

If you depended on one of these for something the public API cannot do, open an issue describing the use
case — that is a gap in the public API, and the fix is to close it there.

## How the surface is kept honest

Every package tracks its public API in two files next to its project file:

- `PublicAPI.Shipped.txt` — the surface as of the last release.
- `PublicAPI.Unshipped.txt` — what has been added since.

The build compares the code against them
([Microsoft.CodeAnalysis.PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md))
and **fails** when a public member is added, changed or removed without the matching edit. A change to the
public surface therefore always shows up as a diff in one of those files, where a reviewer cannot miss it —
and an accidental one (a helper left `public`, a renamed parameter) does not get as far as a pull request.
