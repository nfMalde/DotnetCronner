# Observability

This page documents what DotnetCronner reports about itself: the metrics it publishes, the span it opens
around each execution, the live scheduler state you can read directly, and the health check built on top
of them.

## How it is wired

The scheduler emits on a plain base-class-library `Meter` and `ActivitySource`, both named
`DotnetCronner`. The core package takes **no dependency on OpenTelemetry** to do it, and nothing is
collected — or costs anything — until something subscribes: an unobserved counter is a no-op, and
`StartActivity` returns `null` without allocating when nothing is sampling.

```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m.AddCronnerInstrumentation())
    .WithTracing(t => t.AddCronnerInstrumentation());
```

That comes from the `DotnetCronner.OpenTelemetry` package. It is a convenience, not a requirement — the
one-liners below are exactly equivalent, and need no extra package at all:

```csharp
.WithMetrics(m => m.AddMeter(CronnerDiagnostics.MeterName))
.WithTracing(t => t.AddSource(CronnerDiagnostics.ActivitySourceName))
```

## Metrics

| Instrument | Type | Unit | What it tells you |
| --- | --- | --- | --- |
| `cronner.jobs.started` | counter | `{job}` | Executions started |
| `cronner.jobs.completed` | counter | `{job}` | Executions that finished without throwing |
| `cronner.jobs.failed` | counter | `{job}` | Executions whose job body threw |
| `cronner.jobs.cancelled` | counter | `{job}` | Executions cancelled, or abandoned after losing their lock |
| `cronner.jobs.duration` | histogram | `s` | How long an execution took |
| `cronner.jobs.scheduling.delay` | histogram | `s` | How long after its due time an execution actually started |
| `cronner.jobs.retries` | counter | `{job}` | Retry attempts scheduled after a failure |
| `cronner.jobs.misfired` | counter | `{job}` | Occurrences that came due later than the misfire threshold |
| `cronner.locks.acquired` | counter | `{lock}` | Execution locks claimed by this instance |
| `cronner.locks.lost` | counter | `{lock}` | Locks lost, or left unconfirmable, mid-run |
| `cronner.jobs.running` | up-down counter (observable) | `{job}` | Executions in flight on this instance right now |

**`cronner.jobs.scheduling.delay` is the one to alert on.** Everything else tells you what ran;
this tells you whether the scheduler is keeping up. A delay that climbs means work is coming due faster
than `MaxConcurrentTasks` can absorb it, or that instances are failing to claim — long before anything
actually fails.

### Tags

| Tag | On | Values |
| --- | --- | --- |
| `cronner.task.id` | all metrics | the task's registered id |
| `cronner.outcome` | `duration` | `success`, `failed`, `cancelled`, `lock_lost` |
| `cronner.job.kind` | completion counters | `recurring`, `oneoff` |
| `cronner.misfire.policy` | `misfired` | the policy that handled it |

Cardinality is bounded by design: a task id comes from the registry, and everything else is a short
enumeration. **Nothing a job processes ever becomes a tag** — not payloads, not arguments, not ids from
your own data.

### Suggested histogram buckets

The default bucket boundaries are tuned for HTTP latencies and fit scheduled work badly. Jobs run for
seconds to minutes, so set your own:

```csharp
.WithMetrics(m => m
    .AddCronnerInstrumentation()
    .AddView("cronner.jobs.duration", new ExplicitBucketHistogramConfiguration
    {
        Boundaries = [0.1, 0.5, 1, 5, 15, 60, 300, 900, 3600],
    })
    .AddView("cronner.jobs.scheduling.delay", new ExplicitBucketHistogramConfiguration
    {
        Boundaries = [0.5, 1, 5, 15, 60, 300],
    }));
```

## Tracing

One span per execution attempt, named `cronner.execute <task id>`:

```text
cronner.execute reports:nightly        1.4s
 ├─ db.query  SELECT …                 0.3s
 └─ http POST /api/send                1.0s
```

The span is the **ambient activity** while the job body runs, so the spans the job produces itself — EF
Core queries, outbound HTTP, your own `ActivitySource` — nest underneath it rather than floating free as
orphans. It carries `cronner.task.id`, `cronner.execution.id`, `cronner.attempt`, `cronner.max_attempts`,
`cronner.job.kind` and `cronner.owner`, and ends with `cronner.outcome`. A run that threw gets
`ActivityStatusCode.Error` and the exception recorded as a span event.

A **retry is a separate execution and therefore a separate span**, carrying its own `cronner.attempt` —
consistent with how retries appear everywhere else (see [retries.md](retries.md)).

`cronner.execution.id` is deliberately a span tag only, never a metric tag: it is unique per run, so as a
metric dimension it would be unbounded.

## Reading the scheduler's state directly

`CronnerSchedulerDiagnostics` is a singleton you can resolve anywhere — useful for an admin page, or your
own probe:

| Member | Meaning |
| --- | --- |
| `IsStarted` / `StartedUtc` | whether the hosted service has started, and when |
| `LastPollUtc` / `PollCount` | when the poll loop last completed a pass, and how many it has done |
| `LastPollFaultUtc` / `LastPollError` | the most recent poll failure, if any |
| `RunningCount` | executions in flight on this instance |
| `LongestRunningDuration` | how long the oldest in-flight execution has been going |

A `LastPollUtc` that stops advancing while `IsStarted` is true is the signature of a wedged scheduler —
and it is invisible to every other signal here, because a store can be perfectly healthy while nothing
is being claimed.

## Health checks

From the `DotnetCronner.AspNetCore` package:

```csharp
services.AddHealthChecks().AddCronner();
```

Three probes, in order:

1. **Scheduler started** — otherwise `Unhealthy`. Nothing will ever run.
2. **Still polling** — no completed poll for `MissedPollsAllowed` × `PollingInterval` (default 5 ×) is
   `Unhealthy`. Override the window with `MaxTimeSincePoll`.
3. **Store reachable** — `ICronnerStore.PingAsync`, bounded by `StoreTimeout` (default 5s). Unreachable or
   too slow is `Unhealthy`, and the store's own exception is attached to the result.

Plus one that is off by default:

4. **Stalled run** — set `StalledRunThreshold` and a single execution running longer than it reports
   `Degraded`. Only you know what "too long" means for your jobs, which is why there is no default.

```csharp
services.AddHealthChecks().AddCronner(o =>
{
    o.StalledRunThreshold = TimeSpan.FromMinutes(30);
    o.MissedPollsAllowed = 10;
}, tags: ["ready"]);
```

### What the health check deliberately ignores

**A failing job never makes the application unhealthy.** A task that throws is yours to handle — through
`OnFail`, the `cronner.jobs.failed` metric, or execution history — not a reason for an orchestrator to
restart a process that is otherwise working. The check reports on *the scheduler*, not on the work.

The split between the two failing statuses follows the same logic: `Unhealthy` means the scheduler is
broken and nothing will run until someone intervenes; `Degraded` means work is still moving, just not
well. If the scheduler is not critical to your application, pass
`failureStatus: HealthStatus.Degraded` so even its being down cannot take the process out of rotation.

### Store reachability

`ICronnerStore.PingAsync` is a default interface method, so **every store already has one** — the default
issues one cheap read for an id no real task uses and treats "answered without throwing" as reachable.
The built-in stores override it with something more direct: the Redis store sends `PING`, and the EF Core
store calls `CanConnectAsync`, which reports the database being unreachable rather than the schema being
absent. Override it in a custom store when your backend offers something cheaper, and keep it cheap — a
readiness probe may run every few seconds.
