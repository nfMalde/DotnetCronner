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
    Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default);
    Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default);

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
}
```

`CronnerJob` is a plain, serializable record (Id, Name, Kind, DefinitionId, Payload, PayloadType, Progress,
CronExpression, State, Priority, NextRunUtc, LastRunUtc, RunCount, RetryCount, LastError, LockOwner,
LockedUntilUtc, CreatedUtc, UpdatedUtc). One-off enqueued instances have `Kind == OneOff`, a unique `Id`, a
`DefinitionId` pointing at their template, and a JSON `Payload`. You map it
to and from your own persistence type.

## Three things to get right

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

> For a **single scheduler instance** (the currently supported setup) a normal transaction is enough.
> For multiple instances you must make the claim race-safe at the database level — e.g. `SELECT … FOR
> UPDATE SKIP LOCKED` (Postgres/MySQL), an `UPDLOCK, READPAST` hint (SQL Server), or an optimistic
> concurrency token — so two instances can't claim the same row.

### 3. `RenewLockAsync` — keep the claim alive

While a task runs, the scheduler calls `RenewLockAsync` about every `LockTtl`/2 to push `LockedUntilUtc`
forward, so a long-running job keeps its lock instead of looking stalled and being reclaimed. Extend the
lock **only if the row is still owned by `owner`** (`WHERE Id = id AND LockOwner = owner`), set the new
`LockedUntilUtc`, and return whether a row was updated:

- Return `true` when the update touched the row (still owned) — the run continues.
- Return `false` when it did not (the lock was reclaimed, released, or the job is gone). The scheduler
  reads `false` as "you lost the lock" and **cancels its own run** so the task never executes twice.

The ownership check is the whole point: it must be impossible for a former owner to re-extend a lock that
another worker has already taken over.

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
    Task UpsertAsync(CronnerJobRecord record, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);

    // Selects due, unlocked rows AND locks them for this transaction (e.g. FOR UPDATE SKIP LOCKED),
    // ordered by Priority desc, NextRunUtc asc, limited to `max`.
    Task<IReadOnlyList<CronnerJobRecord>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken ct);
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
            // Extend the lock only while this worker still owns it; return whether a row was updated.
            var updated = await uow.CronnerJobs.RenewLockAsync(id, owner, lockedUntil, ct);
            await uow.SaveChangesAsync(ct);
            return updated > 0;
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
- `UpsertAsync` is insert-or-replace keyed on `Id`.
- `AcquireDueAsync` respects the eligibility rules and priority ordering, sets the lock fields, and is
  atomic (transaction for single-instance; row locking / optimistic concurrency for multi-instance).
- `RenewLockAsync` extends `LockedUntilUtc` only while `LockOwner == owner`, and returns `false` when the
  caller no longer owns the lock.
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
