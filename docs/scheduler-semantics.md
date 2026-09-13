# Scheduler semantics

This page defines, precisely, how DotnetCronner decides what runs and when — the task lifecycle, and the
behavior for scheduled occurrences, manual runs, one-offs, retries, cancellation, lock loss, restart
recovery, in-flight overlap (concurrency), and missed occurrences (misfire).

## Task lifecycle

```text
Scheduled ── the task has a future NextRunUtc
    │
    ▼
Due ─────── NextRunUtc has passed; the task is eligible to be claimed
    │
    ▼
Claimed ─── one worker won an atomic claim (a lease on the task's lock)
    │
    ▼
Running ─── the job body is executing; a heartbeat renews the lease
 ┌──┼───────────────┐
 ▼  ▼               ▼
Succeeded  Failed   Cancelled
            │
            ▼
          Retry ── a NEW execution (new ExecutionId, Attempt + 1), if attempts remain
```

Exactly one worker runs a given scheduled occurrence: the claim is an atomic, lease-based lock
(`AcquireDueAsync`), and a non-concurrent task never runs twice at once. See
[execution-history.md](execution-history.md) for how each run is recorded.

## Scheduled occurrences

A recurring task carries a single `NextRunUtc`. When it passes, the task is claimed and run; on completion
the schedule advances to the next occurrence. How far it advances depends on whether the run was **on time**
or a **misfire** (see below).

## Manual executions

`ICronnerClient.TriggerNowAsync(id)` sets `NextRunUtc = now` and wakes the scheduler; the task runs once,
immediately, then resumes its normal schedule. A trigger that arrives while the task is already running is
queued and fires right after the in-flight run finishes (never overlapping). A manual trigger is never a
misfire.

## One-off jobs

`EnqueueAsync<TPayload>(...)` creates a single-run instance (`CronnerJobKind.OneOff`) with a typed payload.
It runs once and finishes (`Completed`/`Failed`); it is never rescheduled and never treated as a misfire.
Retention prunes finished one-offs (`WithOneOffRetention`).

## Retries

With `DefaultMaxRetries > 0`, a failed run is retried after `RetryDelay`. **A retry is a distinct
execution** — a new `ExecutionId` and `Attempt + 1` — not a re-run of the same execution record. `OnFail`
fires on **each** failed attempt; `ctx.WillRetry` tells a hook whether another attempt is coming, so it can
alert only on the final failure.

## Cancellation

`CancelTaskAsync(id)` cancels a **running** task through its `CancellationToken` (the worker persists the
terminal `Cancelled` state) or, if it is not running, unschedules it. A cancel written by another instance
while the task runs is honored at the run's end rather than being overwritten by the schedule.

## Lock loss

While a task runs, its lease is renewed on a cadence. If a renewal is refused, or cannot be confirmed before
the lease would lapse, the run is **abandoned before the lease expires** — it is cancelled and `OnLockLost`
fires — so another instance can never overlap it. Its history row is finalized `Cancelled` with
`CronnerExecutionErrors.LockLost`. (The residual limit: a job that ignores its token past the abandon margin,
or severe node clock skew — documented under *Execution semantics* in the README.)

## Restart recovery

Task schedules are persisted, so a restart keeps each task's `NextRunUtc`. If the scheduler was down across
one or more occurrences, the stale `NextRunUtc` is detected as a **misfire** when the task is next claimed.
A `Running` history row whose owner died is finalized as `Failed` / `CronnerExecutionErrors.Orphaned` before
the task's next run is recorded.

## In-flight overlap — concurrency

`CronnerConcurrencyMode` governs what happens when an occurrence comes due **while a previous run is still
executing**:

- **DropAndForget** (default) — skip the overlapping occurrence; the task runs again at its next time.
- **Queue** — run the overlapping occurrence after the current one finishes; occurrences never overlap.
- **Concurrent** — allow the new occurrence to run in parallel (the schedule is advanced up front).

## Missed occurrences — misfire

A **misfire** is an occurrence that should have run but didn't because the scheduler was unavailable (down,
or paused past it). It is distinct from in-flight overlap: concurrency governs overlap while *running*; the
`MisfirePolicy` governs the *backlog* that built up while nothing was running.

An occurrence counts as a misfire only once it is later than `CronnerOptions.MisfireThreshold` (default 60s);
a slightly-late normal fire is not a misfire.

| Policy | Behavior |
| --- | --- |
| `FireOnce` (default) | Run one catch-up for the missed window, then resume at the next future occurrence. |
| `Skip` | Run none of the missed occurrences; resume at the next future occurrence. |
| `FireNext` | Identical to `Skip`. |
| `FireAll` | Run **every** missed occurrence in order (sequentially), then resume — bounded by `CronnerOptions.MisfireCatchUpMax` (default 100); older occurrences beyond the cap are dropped with a warning. |

Set it per task — `[CronnerTask(...)] { MisfirePolicy = ... }`, or `WithMisfirePolicy(...)` on a `Sched`
lambda — or globally via `CronnerOptions.DefaultMisfirePolicy`. A task that sets `MisfirePolicy.Default`
(the unset value) inherits the global default.

### Interaction with concurrency

`FireAll` catches up **sequentially** under `DropAndForget` and `Queue` (each catch-up run completes before
the next is claimed — deterministic, single-run-per-id). Under **`Concurrent`**, the schedule is advanced up
front on claim, so a misfire always resolves to a **single** catch-up regardless of the policy — if you need
`FireAll` to drain a backlog, use `DropAndForget` or `Queue`.

A worked example — `every 5 minutes`, runtime 20 minutes, `Queue`, `FireAll` after an outage — drains its
backlog one run at a time in order, then returns to its normal cadence.
