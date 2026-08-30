# Execution history

DotnetCronner can record **execution history** — one row per run of a task — so you can see what ran, when,
how long it took, and how it ended. This page defines the model, its lifecycle, retention, and how a store
persists it. History is **opt-in**: `WithExecutionHistory(keepPerTask)` (0, the default, records nothing).

## The model

A registered job has **many** executions:

```text
CronnerJobEntity  (the registered/scheduled job — one row per task id)
      │
      └── has many
              │
              ▼
        JobExecutionEntity  (one row per run: execution #1, #2, #3, …)
```

- **`CronnerJob` / `CronnerJobEntity`** — the persisted task: its id, cron, state, next run, lock. One per
  task id (a recurring task, or a one-off instance).
- **`CronnerJobExecution` / `JobExecutionEntity`** — one concrete run of that job. This is what execution
  history is made of.

`CronnerJobExecution` (the domain shape the store returns) carries:

| Field | Meaning |
| --- | --- |
| `Id` | the **execution id** — a stable `string` (a `Guid`-N) generated when the run starts; correlates the start and finish records, and is what `ctx.ExecutionId` exposes to the job and every hook of the run |
| `JobId` | the owning `CronnerJob.Id` (a recurring task id, or a one-off instance id) |
| `StartedAt` / `FinishedAt` | run start (UTC) / finish (UTC, `null` while running) |
| `Duration` | `FinishedAt - StartedAt`, or `null` while running — **computed**, so it never disagrees with the timestamps |
| `Status` | `Running` → `Succeeded` / `Failed` / `Cancelled` (see the lifecycle) |
| `Attempt` | the 1-based attempt number; a retry is a **new** execution with a new `Id` and the next `Attempt` |
| `Error` | the failure message, or a well-known marker (`CronnerExecutionErrors.Orphaned` / `.LockLost`) |
| `Owner` | the scheduler instance that ran it — "which node ran this" |

Read a task's recent runs with `ICronnerClient.GetExecutionsAsync(taskId, limit)` (newest first).

## Lifecycle

```text
        run starts
            │
            ▼
        Running ───────────────┐ (never finalized → treated as orphaned/lost)
       ┌──┼───────────────┐    │
       ▼  ▼               ▼    ▼
   Succeeded  Failed   Cancelled  → finalized as Failed (Orphaned) or Cancelled (LockLost)
                 │
                 ▼
               Retry = a NEW execution (new Id, Attempt + 1)
```

- A run **inserts** a `Running` record at start and **finalizes** the same record (matched on `Id`) to a
  terminal status at the end.
- A `Running` record that is never finalized means the run crashed or its worker died. The scheduler
  **finalizes such orphans** as `Failed` with `CronnerExecutionErrors.Orphaned` before it records the task's
  next run; a run abandoned for a lost/unconfirmable lock finalizes its own row as `Cancelled` with
  `CronnerExecutionErrors.LockLost`.
- A **retry** is a distinct execution — a new `Id`, `Attempt` incremented — not an update of the failed one.

## The abstraction is store-independent

`JobExecutionEntity` is an abstract base a store maps. It deliberately carries **only the run's intrinsic
fields** (timestamps, status, attempt, error, owner) and **no primary key and no job foreign key**:

- The scheduler never uses an execution's surrogate key — it correlates through the string `Id`
  (`ExecutionId`) — so the key's **type is the store's/database's choice**. There is no way for the library
  to know whether your database keys with `int`, `long`, `Guid`, or `string`, so it does not impose one. A
  fixed-type key on the base would dictate a choice the abstraction has no stake in.
- Each store subclass adds the key and the job link it needs. The **EF Core** store's
  `CronnerJobExecutionEntity` adds a numeric identity `Id` + a `TaskId` foreign key (with `ON DELETE
  CASCADE`) + an indexed correlation id. The **Redis** store serializes the domain record under a per-job
  key. A **custom** store maps it however its technology prefers.

Override the `virtual` `Apply(CronnerJobExecution)` / `ToDomain(...)` on your subclass to map extra columns.

## Retention

`WithExecutionHistory(keepPerTask)` keeps the newest N runs **per task**; older rows are pruned after each
run. `0` disables recording entirely. The store implements the trim (`PruneExecutionsAsync`); removing a job
removes its history.

## What execution history is **not**

Execution history records **an execution** — not a general-purpose application log or a place to stash
business data. Keep application-specific data and logs in **your own store**, correlated to a run by its
`ExecutionId`:

```csharp
// in the job / a hook — ctx.ExecutionId is the history row's Id, visible to every hook of the run
myRunStore.Save(ctx.ExecutionId, new MySummary(...));
```

> The older `ctx.SetExecutionData(...)` / `TryGetExecutionData<T>()` and the `Data` slot on the execution
> record are **deprecated** for this reason and will be removed in a future release. Migrate app data into
> your own store keyed by `ExecutionId`. (The `DotnetCronner.Sample.WebApi` sample shows the pattern: a
> `RunSummaryStore` behind `GET /runs`.)

## Per store

| Store | History | Schema |
| --- | --- | --- |
| In-memory | ✅ (lost on restart) | — |
| EF Core | ✅ `CronnerJobExecutions` table | run `dotnet ef migrations add …` once, when you first enable it |
| Redis | ✅ per-job hash | — |
| Custom | override the `RecordExecution*` / `GetExecutions` / `PruneExecutions` / `FinalizeOrphanedExecutions` methods | your call |
