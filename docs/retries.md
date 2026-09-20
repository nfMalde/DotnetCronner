# Retries

This page defines how DotnetCronner retries a task that fails: how attempts are counted, how the wait
between them is calculated, where a policy is configured and which one wins, and what a job and its hooks
can see about the attempt they are in.

## The model

A **retry policy** answers two questions: how many attempts a failing task gets, and how long to wait
before each one.

```csharp
cronner.Sched<ReportJobs>(x => x.SendAsync(x.HasParam<CancellationToken>()), o => o
    .WithCron("0 * * * *")
    .WithRetryPolicy(p => p
        .MaxAttempts(5)
        .ExponentialBackoff(TimeSpan.FromSeconds(5))
        .WithJitter()));
```

`MaxAttempts` counts **total attempts including the first run**. `MaxAttempts(5)` is one run plus four
retries; `MaxAttempts(1)` — the default when nothing is configured — means the task is never retried.

Each attempt is a **separate execution**: it gets its own `ExecutionId`, its own history row, and an
`Attempt` one higher than the last. A retry is never confused with a fresh cron occurrence — while
attempts remain the task is rescheduled at the retry delay rather than at its next cron time, and the cron
schedule resumes only once an attempt succeeds or the attempts run out.

## Strategies

| Strategy | Wait before each retry |
| --- | --- |
| `Immediate` | none — retry as soon as the scheduler picks the task up again |
| `Fixed` | the configured delay, every time |
| `Exponential` | the configured delay, doubling after each failed attempt, capped by `MaxDelay` |

**Jitter is not a strategy** — it is a modifier that composes with any of them, so "exponential backoff
with jitter" is `ExponentialBackoff(...)` plus `WithJitter()`. With jitter on, the actual wait is drawn
uniformly from `[delay/2, delay]`: never longer than the delay you configured, never collapsing to zero,
but enough spread that a hundred tasks knocked over by the same database outage do not all retry at the
same instant.

`MaxDelay` (default: one hour) is the ceiling on **exponential growth** — without it, a long run of
failures pushes the next attempt arbitrarily far out. It does not clamp a `FixedDelay`: a delay written
out literally is used as written.

```text
ExponentialBackoff(5s), MaxDelay 30s
attempt 1 fails → wait 5s
attempt 2 fails → wait 10s
attempt 3 fails → wait 20s
attempt 4 fails → wait 30s   (40s would be past the cap)
```

## Where a policy comes from

Three places, most specific first:

1. **The task's own policy** — `WithRetryPolicy(...)` on a `Sched` registration, or the retry properties on
   `[CronnerTask]`.
2. **The scheduler default** — `WithRetryPolicy(...)` on the builder, or `CronnerOptions.DefaultRetryPolicy`.
3. **The `DefaultMaxRetries` / `RetryDelay` shorthand** — used only while no policy is set anywhere.

The shorthand still works exactly as it always did, so an application that never touches policies keeps the
retry behavior it already has. Note the off-by-one between the two vocabularies: `DefaultMaxRetries` counts
**retries**, `MaxAttempts` counts **attempts**, so `DefaultMaxRetries = 2` is the same thing as
`MaxAttempts(3)`.

### On the attribute

An attribute cannot carry a lambda or a `TimeSpan`, so the same policy is expressed as plain properties:

```csharp
[CronnerTask(cronstring: "0 * * * *",
    MaxAttempts = 5,
    RetryStrategy = CronnerRetryStrategy.Exponential,
    RetryDelaySeconds = 5,
    RetryMaxDelaySeconds = 300,
    RetryJitter = true)]
public Task SendAsync(IReportService reports, CancellationToken ct) => reports.SendAsync(ct);
```

`MaxAttempts` is required as soon as any other retry property is set — the rest only describe *how* to
retry, and a task that looks retried but never is would be worse than a startup error. Leave all of them
unset to inherit the scheduler default.

## What an attempt can see

The job body reads its retry context from `ICronnerJobContext`, hooks from `CronnerTaskContext`:

| Member | Job | Hook | Meaning |
| --- | --- | --- | --- |
| `Attempt` | ✓ | ✓ | this attempt's 1-based number (`1` is the first run) |
| `MaxAttempts` | ✓ | ✓ | total attempts the effective policy allows |
| `PreviousError` | ✓ | ✓ | the message of the failure that caused this retry; `null` on the first attempt |
| `WillRetry` | | ✓ | on `OnFail`, whether another attempt is coming |
| `RetryDelay` | | ✓ | on `OnFail`, the wait before the next attempt; `null` when none follows |

```csharp
public async Task SendAsync(IReportService reports, ICronnerJobContext ctx, CancellationToken token)
{
    if (ctx.Attempt > 1)
        _logger.LogWarning("Attempt {Attempt} of {Max}; last failure: {Error}",
            ctx.Attempt, ctx.MaxAttempts, ctx.PreviousError);

    await reports.SendAsync(token);
}
```

`PreviousError` is the message only. The exception object itself does not survive the wait between
attempts — a retry is re-dispatched from the store, possibly by a different instance — so only the message
is carried across. The *current* attempt's own exception is `ctx.Exception` on a hook, as always.

`RetryDelay` is the delay actually used to reschedule, not a fresh calculation: with jitter enabled the
wait is drawn once, reported to the hook, and then applied, so what a hook logs is what really happens.

## Hooks and retries

`OnFail` fires on **each** failed attempt, not once after the attempts are exhausted. Check `ctx.WillRetry`
to alert only on the final failure:

```csharp
cronner.OnFail(ctx =>
{
    if (ctx.WillRetry)
        return Task.CompletedTask;   // another attempt is coming; stay quiet

    return alerts.RaiseAsync($"{ctx.Job.Id} failed after {ctx.Attempt} attempts: {ctx.Exception?.Message}");
});
```

## Interaction with the rest of the scheduler

- **Misfire.** A retry is never treated as a missed occurrence. Its due time is the retry instant, not a
  cron time, so the misfire policy (`FireOnce` / `Skip` / `FireAll` / `FireNext`) never walks a cron backlog
  on behalf of a retry. See [scheduler-semantics.md](scheduler-semantics.md).
- **Concurrency.** Attempts of one occurrence are sequential by construction — the next attempt is scheduled
  only once the previous one has finished — so a retry never overlaps the attempt it follows. The exception is
  `CronnerConcurrencyMode.Concurrent`: such a run advances the schedule up front and only records its outcome,
  so **a `Concurrent` task is not retried**. Its hooks report this honestly (`MaxAttempts` is 1 and
  `WillRetry` is `false`) rather than promising an attempt that never arrives. Build the retry into the job
  body itself if a `Concurrent` task needs one.
- **One-off jobs.** An enqueued one-off is retried like any other task; once its attempts are exhausted it
  finishes as `Failed` and is never rescheduled.
- **Restarts.** The attempt count lives on the persisted task (`RetryCount`), so a scheduler restart during
  a retry delay resumes at the right attempt rather than starting over.
