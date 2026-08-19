# Writing a custom store

DotnetCronner persists tasks through a single interface, `ICronnerStore`. Implement it and you can back
the scheduler with any data layer — Dapper, NHibernate, MongoDB, a raw connection, or your app's existing
UnitOfWork/repository — without EF Core or Redis.

```csharp
public interface ICronnerStore
{
    Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default);
    Task UpsertAsync(CronnerJob job, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
    // The execution lock — the three methods that own LockOwner / LockedUntilUtc (see "Two things to get right").
    Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default);
    Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default);
    Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken ct = default);   // default impl: read → owner check → Upsert; override it (see §4)

    // Optional per-run lifecycle (default no-op) — open/close a per-run session if your store needs one.
    Task OnStartAsync(CronnerJob job, CancellationToken ct = default) => Task.CompletedTask;
    Task OnCloseAsync(CronnerJob job, CancellationToken ct = default) => Task.CompletedTask;

    // Optional (default no-op) — implement for one-off retention and persisted progress.
    Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken ct = default) => Task.CompletedTask;
    Task UpdateProgressAsync(string id, decimal progress, CancellationToken ct = default) => Task.CompletedTask;

    // Optional (default no-op / empty) — implement to persist execution history (WithExecutionHistory).
    Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken ct = default) => Task.CompletedTask;
    Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken ct = default) => Task.CompletedTask;
    Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(string jobId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CronnerJobExecution>>(Array.Empty<CronnerJobExecution>());
    Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken ct = default) => Task.CompletedTask;
    Task<int> FinalizeOrphanedExecutionsAsync(string jobId, DateTimeOffset finishedAt, string error, CancellationToken ct = default) => Task.FromResult(0);
}
```

`CronnerJob` is a plain, serializable record (Id, Name, Kind, DefinitionId, Payload, PayloadType, Progress,
CronExpression, State, Priority, NextRunUtc, LastRunUtc, RunCount, RetryCount, LastError, LockOwner,
LockedUntilUtc, CreatedUtc, UpdatedUtc). One-off enqueued instances have `Kind == OneOff`, a unique `Id`, a
`DefinitionId` pointing at their template, and a JSON `Payload`. You map it
to and from your own persistence type.

## Five things to get right

Four of them are the **execution lock** — the mechanism behind "one run per task across processes". The
contract in a sentence: **`LockOwner` and `LockedUntilUtc` are changed only by `AcquireDueAsync`,
`RenewLockAsync` and `ReleaseLockAsync`, each of them atomically and conditionally on the current owner;
nothing else — `UpsertAsync` included — ever touches them.** The shipped stores (EF Core on PostgreSQL and SQL
Server, Redis) are held to exactly this contract by the shared test suites `StoreLockContractTests` and
`SchedulerExclusivityTests` in `tests/DotnetCronner.Tests/Shared`, and so can yours: point the abstract
classes at your store (one `IStoreBackend` implementation) and you get the contended-claim, renew/release,
stale-upsert and two-scheduler tests for free — that is the evidence to look at before you delete your own
"already running" guard.

### 1. Lifetime — declare it

`UseStore<TStore>()` defaults to `CronnerStoreLifetime.Singleton`: your store is built **once** and reused
for the application's lifetime. The scheduler polls while jobs run, so its methods overlap — a singleton
store must be safe for concurrent use.

That makes a constructor-injected `DbContext`, ORM session or open connection wrong by default: one instance
would serve overlapping operations, which fails with *"a command is already in progress"* (Npgsql) or
*"a second operation was started on this context instance"* (EF Core). It typically only appears once enough
tasks are registered for the seeding fan-out to overlap, so it hides in small samples and shows up under
load.

Two correct options, in order of preference:

**Declare the store scoped** — it is then built per scheduler operation, from that operation's DI scope, so
constructor injection of a scoped resource is safe and reads naturally:

```csharp
app.UseDotnetCronner(c => c.UseStore<MyStore>(CronnerStoreLifetime.Scoped));

public sealed class MyStore(AppDbContext db) : ICronnerStore { /* ... */ }
```

**Or keep it a singleton and hold a factory**, creating the short-lived resource per call. This is what the
built-in EF Core store does with `IDbContextFactory<T>`:

```csharp
public sealed class MyStore(IDbContextFactory<AppDbContext> factory) : ICronnerStore
{
    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // ...
    }
}
```

Either way the rule is the same: **one scheduler operation must never share a non-concurrent resource with
another.** Pick whichever expresses that more clearly for your storage technology.

### 2. `AcquireDueAsync` — the atomic claim

Every other method is a straightforward read/write. `AcquireDueAsync` is the heart of the scheduler: it
must **find due tasks and claim them so the same task is never handed out twice.** A task is eligible when:

- `State != Cancelled`, **and**
- `NextRunUtc != null && NextRunUtc <= now`, **and**
- its lock is free or expired: `LockOwner == null || LockedUntilUtc == null || LockedUntilUtc < now`.

Order the eligible tasks by **`Priority` descending, then `NextRunUtc` ascending**, take up to `max`, and
for each one set `State = Queued`, `LockOwner = owner`, `LockedUntilUtc = now + lockTtl`,
`UpdatedUtc = now`, and persist. Returning them under `lockTtl` also gives crash recovery: if a worker
dies, the lock expires and the task becomes eligible again.

The claim must be **race-safe against other instances calling it at the same instant**: the eligibility
check and the write of the new owner have to happen atomically per row. Any of these works:

- a **conditional `UPDATE` that re-asserts eligibility in its `WHERE`** (the built-in EF Core store: read
  candidates without locks, then per candidate `UPDATE … SET LockOwner = @owner … WHERE Id = @id AND
  <eligibility predicate>`; the statement that affects 0 rows lost the race — correct at READ COMMITTED on
  PostgreSQL, SQL Server (with and without snapshot), MySQL and SQLite, no transaction or concurrency token
  needed);
- `SELECT … FOR UPDATE SKIP LOCKED` (PostgreSQL/MySQL) or an `UPDLOCK, READPAST` hint (SQL Server) inside a
  transaction (the UnitOfWork example below);
- a `SET NX` on a per-task lock key (the Redis store);
- an optimistic concurrency token on the row.

What is **not** safe is "read the due rows, then write them unconditionally" — two instances reading the
same page would both claim the same task. Also re-check eligibility in the claim itself (`State <> Cancelled`,
still due) rather than trusting the candidate read. Two practical notes: claim **highest `Priority` first**
(then earliest `NextRunUtc`), and when other instances won every candidate you looked at, look further
(re-read / next page, bounded) so an instance with free capacity is not starved by a head-of-line full of
rows that were just taken.

### 3. `RenewLockAsync` — keep the claim alive

While a task runs, the scheduler calls `RenewLockAsync` about every `LockTtl`/2 to push `LockedUntilUtc`
forward, so a long-running job keeps its lock instead of looking stalled and being reclaimed. Extend the
lock **only if the row is still owned by `owner` and the task is not `Cancelled`**
(`WHERE Id = id AND LockOwner = owner AND State <> Cancelled`), set the new `LockedUntilUtc`, and return
whether a row was updated:

- Return `true` when the update touched the row (still owned) — the run continues.
- Return `false` when it did not — the lock was reclaimed, released, the job is gone, **or the task was set
  to `Cancelled`** (a cancel issued from another instance). The scheduler reads `false` as "stop this run":
  it cancels its own run, then looks at the job to tell the two cases apart (`Cancelled` → an ordinary
  `OnCancel`; otherwise `OnLockLost`).
- **Throw when you cannot tell** — the database is unreachable, the connection dropped mid-call. Do **not**
  return `false` for that (one blip would kill an hour-long job) and do not swallow it and pretend success
  (the lease would silently lapse under a run that believes it owns the task). The scheduler owns this case:
  it keeps the run alive while the expiry it last confirmed is still ahead, retries at a tighter cadence, and
  abandons the run *before* that expiry can lapse — so a store outage does not by itself produce a second
  concurrent run (what remains is the job's own cancellation latency and clock skew, see the lease rule in
  the README), and a transient blip does not cost a job. Your renewal needs no state of its own for this; it
  just has to be honest about what it knows.

The ownership check is the whole point: it must be impossible for a former owner to re-extend a lock that
another worker has already taken over.

### 4. `ReleaseLockAsync` — give the claim back, owner-conditionally

When a run ends the scheduler persists the outcome (`UpsertAsync`) and then releases the claim with
`ReleaseLockAsync(id, owner)`. Clear `LockOwner`/`LockedUntilUtc` **only while the row is still owned by
`owner`** (`UPDATE … SET LockOwner = NULL, LockedUntilUtc = NULL WHERE Id = id AND LockOwner = owner`) and
return whether a row was updated. A former owner whose claim was reclaimed by someone else must get `false`
and change nothing — otherwise it would free the new owner's lock while that instance is still running.

The interface ships a default implementation (read the job, check the owner, clear, `UpsertAsync`). It only
works for a store whose `UpsertAsync` still overwrites lock fields; once you implement §5 you **must**
override it with the atomic statement above.

### 5. `UpsertAsync` — never touch a lock you did not take

`UpsertAsync` is insert-or-update keyed on `Id`, and on an **update it must keep the row's current
`LockOwner`/`LockedUntilUtc`** and ignore the values on the incoming job (on an insert, take them as-is —
normally `null`). The reason is a race that is narrow but real: `TriggerNowAsync`, the seeding pass at
startup and `CancelTaskAsync` all read a job and write it back a moment later. If another instance claimed
the task in between, a full-row write would clear (or shorten) that instance's lock — and a third instance
could claim the task while the second is still running it. Lock fields therefore have exactly three writers
(§2–§4); everything else is hands-off. In SQL this is simply an `UPDATE` that lists every column *except*
the two lock columns; with an ORM, re-apply the entity's current lock values after mapping the incoming job.

(`UpdateProgressAsync` follows the same rule for the same reason: it touches `Progress` only.)

## Optional: a per-run session (`OnStartAsync` / `OnCloseAsync`)

Both have a default no-op implementation, so implement them only if your store wants a session / unit of
work / transaction that spans a single job run. `OnStartAsync` is called **before any other store call for
that run**; `OnCloseAsync` is called **at the very end, always — even if the run failed** (check
`job.State` / `job.LastError` there to decide commit vs rollback).

Because a given task id does not run twice at once by default, you can key the per-run session by
`job.Id` and let the other methods pick it up (falling back to a transient scope when there is no active
run — e.g. calls made through `ICronnerClient`):

```csharp
private readonly ConcurrentDictionary<string, IUnitOfWork> _sessions = new();

public Task OnStartAsync(CronnerJob job, CancellationToken ct = default)
{
    _sessions[job.Id] = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IUnitOfWork>();
    return Task.CompletedTask;
}

public async Task OnCloseAsync(CronnerJob job, CancellationToken ct = default)
{
    if (_sessions.TryRemove(job.Id, out var uow))
    {
        if (job.State != CronnerTaskState.Failed) await uow.SaveChangesAsync(ct);
        uow.Dispose();
    }
}

// In UpsertAsync/RenewLockAsync/etc.: use _sessions.TryGetValue(job.Id, out var uow) ? uow : a fresh scope.
```

> Concurrent-mode tasks (`CronnerConcurrencyMode.Concurrent`) can overlap, so per-id keying is meant for
> the default and `Queue` modes; a concurrent task should not rely on a single per-run session.

## Example: over a UnitOfWork

Assume your app already exposes a scoped UnitOfWork with a repository (this is ORM-agnostic — swap in
Dapper, NHibernate, etc.):

```csharp
public interface IUnitOfWork
{
    ICronnerJobRepository CronnerJobs { get; }
    Task<ITransaction> BeginTransactionAsync(CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ICronnerJobRepository
{
    Task<CronnerJobRecord?> FindAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<CronnerJobRecord>> QueryAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct);
    Task UpsertAsync(CronnerJobRecord record, CancellationToken ct);          // insert, or update every column EXCEPT LockOwner/LockedUntilUtc
    Task RemoveAsync(string id, CancellationToken ct);

    // Selects due, unlocked rows AND locks them for this transaction (e.g. FOR UPDATE SKIP LOCKED),
    // ordered by Priority desc, NextRunUtc asc, limited to `max`.
    Task<IReadOnlyList<CronnerJobRecord>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken ct);

    // UPDATE … SET LockedUntilUtc = @until WHERE Id = @id AND LockOwner = @owner AND State <> Cancelled  → rows affected
    Task<int> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct);
    // UPDATE … SET LockOwner = NULL, LockedUntilUtc = NULL WHERE Id = @id AND LockOwner = @owner          → rows affected
    Task<int> ReleaseLockAsync(string id, string owner, CancellationToken ct);
}
```

The store:

```csharp
using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;

public sealed class UnitOfWorkCronnerStore : ICronnerStore
{
    private readonly IServiceScopeFactory _scopeFactory;   // NOT a scoped UnitOfWork!

    public UnitOfWorkCronnerStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    private async Task<T> InScopeAsync<T>(Func<IUnitOfWork, Task<T>> body)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await body(uow);
    }

    public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) =>
        InScopeAsync(async uow => (await uow.CronnerJobs.FindAsync(id, ct))?.ToDomain());

    public Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken ct = default) =>
        InScopeAsync(async uow =>
        {
            var rows = await uow.CronnerJobs.QueryAsync(state, offset, limit, ct);
            return (IReadOnlyList<CronnerJob>)rows.Select(r => r.ToDomain()).ToArray();
        });

    public Task UpsertAsync(CronnerJob job, CancellationToken ct = default) =>
        InScopeAsync<object?>(async uow =>
        {
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            await uow.CronnerJobs.UpsertAsync(CronnerJobRecord.From(job), ct);
            await uow.SaveChangesAsync(ct);
            return null;
        });

    public Task RemoveAsync(string id, CancellationToken ct = default) =>
        InScopeAsync<object?>(async uow =>
        {
            await uow.CronnerJobs.RemoveAsync(id, ct);
            await uow.SaveChangesAsync(ct);
            return null;
        });

    public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default) =>
        InScopeAsync(async uow =>
        {
            await using var tx = await uow.BeginTransactionAsync(ct);

            // ClaimDueAsync locks the due rows for this transaction (FOR UPDATE SKIP LOCKED),
            // already ordered by Priority desc, NextRunUtc asc, and limited to `max`.
            var due = await uow.CronnerJobs.ClaimDueAsync(now, max, ct);

            var claimed = new List<CronnerJob>(due.Count);
            foreach (var record in due)
            {
                record.State = CronnerTaskState.Queued;
                record.LockOwner = owner;
                record.LockedUntilUtc = now + lockTtl;
                record.UpdatedUtc = DateTimeOffset.UtcNow;
                await uow.CronnerJobs.UpsertAsync(record, ct);
                claimed.Add(record.ToDomain());
            }

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (IReadOnlyList<CronnerJob>)claimed;
        });

    public Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default) =>
        InScopeAsync(async uow =>
        {
            // Extend the lock only while this worker still owns it (and the task is not cancelled); return
            // whether a row was updated. A connection failure simply propagates — "cannot tell" is an exception,
            // never `false`; the scheduler keeps the run alive while its last confirmed lease is still ahead.
            var updated = await uow.CronnerJobs.RenewLockAsync(id, owner, lockedUntil, ct);
            await uow.SaveChangesAsync(ct);
            return updated > 0;
        });

    public Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken ct = default) =>
        InScopeAsync(async uow =>
        {
            // Owner-conditional: a former owner cannot free a lock that someone else holds now.
            var released = await uow.CronnerJobs.ReleaseLockAsync(id, owner, ct);
            await uow.SaveChangesAsync(ct);
            return released > 0;
        });
}
```

Your persistence type just mirrors `CronnerJob` and maps both ways. Abstractions already ships a
ready-made one you can reuse or subclass — **`CronnerJobEntity`** — a plain, mutable class with
`virtual` properties (so ORMs like NHibernate can proxy it) and `ToDomain()` / `From(job)` / `Apply(job)`
helpers. Its key is a **`string TaskId`** (the value of `CronnerJob.Id`) — deliberately *not* named `Id`,
so it never clashes with an `int`/`long` surrogate-key convention on your own entities or base classes.
Use it instead of the hand-rolled `CronnerJobRecord` below if it fits your data layer.

The only hard requirement is that your persistence type round-trips the task's **string** id to
`CronnerJob.Id`. So if your conventions demand a surrogate PK, give your record its own `int`/`long` `Id`
and store the task id in a separate (unique) string column — the store maps between the two:

```csharp
public sealed class CronnerJobRecord
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public CronnerJobKind Kind { get; set; }        // Recurring or OneOff
    public string? DefinitionId { get; set; }       // one-off → the template id it runs
    public string? Payload { get; set; }            // one-off → JSON payload
    public string? PayloadType { get; set; }
    public decimal Progress { get; set; }           // last reported total progress
    public string? CronExpression { get; set; }
    public CronnerTaskState State { get; set; }
    public CronnerTaskPriority Priority { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public int RunCount { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? LockOwner { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public CronnerJob ToDomain() => new()
    {
        Id = Id, Name = Name, Kind = Kind, DefinitionId = DefinitionId, Payload = Payload,
        PayloadType = PayloadType, Progress = Progress, CronExpression = CronExpression, State = State,
        Priority = Priority, NextRunUtc = NextRunUtc, LastRunUtc = LastRunUtc, RunCount = RunCount,
        RetryCount = RetryCount, LastError = LastError, LockOwner = LockOwner, LockedUntilUtc = LockedUntilUtc,
        CreatedUtc = CreatedUtc, UpdatedUtc = UpdatedUtc,
    };

    public static CronnerJobRecord From(CronnerJob j) => new()
    {
        Id = j.Id, Name = j.Name, Kind = j.Kind, DefinitionId = j.DefinitionId, Payload = j.Payload,
        PayloadType = j.PayloadType, Progress = j.Progress, CronExpression = j.CronExpression, State = j.State,
        Priority = j.Priority, NextRunUtc = j.NextRunUtc, LastRunUtc = j.LastRunUtc, RunCount = j.RunCount,
        RetryCount = j.RetryCount, LastError = j.LastError, LockOwner = j.LockOwner, LockedUntilUtc = j.LockedUntilUtc,
        CreatedUtc = j.CreatedUtc, UpdatedUtc = j.UpdatedUtc,
    };
}
```

## Register it

```csharp
builder.Services.AddScoped<IUnitOfWork, MyUnitOfWork>();   // your app's data layer
builder.Services.AddDotnetCronner(c => c.UseStore<UnitOfWorkCronnerStore>());
```

`UseStore<T>()` is mutually exclusive with `UseRedisAsStore` / `UseEntityFrameworkStore` (configuring
more than one store throws). That's all — scheduling, hooks, the `ICronnerClient`, priority and
concurrency modes all run unchanged on top of your store.

## Checklist

- Inject `IServiceScopeFactory`, not a scoped UnitOfWork.
- `GetByIdAsync` returns `null` when missing; `GetAsync` filters by `state`, applies `offset`/`limit`,
  ordered oldest-first is fine.
- `UpsertAsync` is insert-or-update keyed on `Id` — and on an update it **leaves `LockOwner`/`LockedUntilUtc`
  alone** (§5); only the three lock methods write them.
- `AcquireDueAsync` respects the eligibility rules and priority ordering, sets the lock fields, and claims
  **atomically against other instances** (conditional `UPDATE` re-asserting eligibility / `SKIP LOCKED` /
  `SET NX` / concurrency token — never read-then-write-unconditionally), §2.
- `RenewLockAsync` extends `LockedUntilUtc` only while `LockOwner == owner` and the task is not `Cancelled`;
  returns `false` when the caller definitively does not hold the claim (reclaimed / released / gone /
  cancelled), and **throws** when it cannot tell (§3).
- `ReleaseLockAsync` clears the lock only while `LockOwner == owner` (§4) — override the default once your
  `UpsertAsync` preserves lock fields.
- Run the shared `StoreLockContractTests` / `SchedulerExclusivityTests` against your store (implement
  `IStoreBackend` for it) before relying on the cross-process guarantee.
- `OnStartAsync` / `OnCloseAsync` are optional — implement them only for a per-run session, and make sure
  `OnCloseAsync` disposes/commits what `OnStartAsync` opened.
- Map every `CronnerJob` field (incl. `Kind`, `DefinitionId`, `Payload`, `PayloadType`, `Progress`) so
  nothing is silently dropped across a restart.
- `PruneCompletedOneOffsAsync` / `UpdateProgressAsync` are optional (default no-ops) — implement them for
  one-off retention and persisted progress. `UpdateProgressAsync` must touch *only* `Progress` (never the
  lock fields), so it can't race the keepalive.
- The execution-history methods are optional (default no-op / empty), only exercised when
  `WithExecutionHistory(keepPerTask)` is set. `RecordExecutionStartedAsync` inserts a `Running`
  `CronnerJobExecution`; `RecordExecutionFinishedAsync` finalizes *that same record*, matched on its
  `Id` (a per-run correlation id — don't key history by `JobId` alone, since concurrent runs share it);
  `GetExecutionsAsync` returns them newest-first; `PruneExecutionsAsync` keeps the newest `keepNewest` per
  job. Deleting a job should also drop its history. `JobExecutionEntity` is a ready-to-map base (add your
  own key + job link), mirroring `CronnerJobEntity`.
- `FinalizeOrphanedExecutionsAsync(jobId, finishedAt, error)` (default no-op, returns 0) sets every
  still-`Running` record of that job to `Failed` with the given `FinishedAt`/`Error` and returns the count —
  one targeted `UPDATE … WHERE JobId = @jobId AND Status = Running`. The scheduler calls it right before it
  records a new run of a non-concurrent task (holding the lock proves older `Running` rows are orphans of a
  crashed owner), with `error = CronnerExecutionErrors.Orphaned`. Implement it so history stays
  self-consistent after a crash without every consumer hand-rolling a sweeper.
