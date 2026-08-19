using CronTestApp.Configuration;
using CronTestApp.Jobs;
using CronTestApp.Services;
using CronTestApp.Stores;
using DotnetCronner;

namespace CronTestApp.Endpoints;

/// <summary>
/// The management surface. DotnetCronner ships no dashboard on purpose, so everything goes through
/// <see cref="ICronnerClient"/> on endpoints this app owns (and, in a real app, secures).
/// </summary>
public static class CronnerEndpoints
{
    /// <summary>Maps the task management and observability endpoints.</summary>
    public static WebApplication MapCronnerTestEndpoints(this WebApplication app, TestAppOptions options)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            app = "DotnetCronner test harness",
            configuration = options.Describe(),
            endpoints = new[]
            {
                "GET  /config                  — the active .env-driven configuration",
                "GET  /tasks?state=&offset=&limit=  — persisted task rows",
                "GET  /registered              — every registered definition (incl. never-run / manual)",
                "GET  /tasks/{id}              — one task",
                "GET  /tasks/{id}/history?take= — execution history for a task (needs CRONNER_EXEC_HISTORY>0)",
                "POST /tasks/{id}/run          — TriggerNow: run a task immediately (works for manual tasks)",
                "POST /enqueue/notify          — enqueue a one-off with a typed payload (body: {to,message,attempt})",
                "POST /tasks/{id}/cancel       — cancel a running task and unschedule it",
                "POST /tasks/{id}/steal-lock   — release a running task's claim on its owner's behalf to force OnLockLost",
                "POST /store/renewal-outage?seconds= — (custom store) make lock renewals throw for N seconds: the lease rule, live",
                "GET  /activity?take=&jobId=   — what the jobs actually did (newest first)",
                "GET  /metrics                 — per-task counters, incl. lock events, from the hooks",
                "GET  /progress                — live progress, rebuilt purely from the progress hooks",
                "GET  /locks                   — measured keepalive cadence vs. the expected LockTtl/2",
                "GET  /healthz                 — liveness",
            },
        }));

        app.MapGet("/config", () => Results.Ok(options.Describe()));

        app.MapGet("/tasks", async (ICronnerClient client, CronnerTaskState? state, int offset = 0, int limit = 100) =>
            Results.Ok(await client.GetTasksAsync(state, offset, limit)));

        // Every registered definition, whether or not it has ever run — includes manual / enqueue-only jobs.
        app.MapGet("/registered", (ICronnerClient client) => Results.Ok(client.GetRegisteredTasks()));

        app.MapGet("/tasks/{id}", async (ICronnerClient client, string id) =>
            await client.GetTaskByIdAsync(id) is { } task
                ? Results.Ok(task)
                : Results.NotFound(new { error = $"No task with id '{id}'." }));

        // Execution history for a task, newest first. Empty unless CRONNER_EXEC_HISTORY > 0 — each run then
        // records a Running row on start that is finalized (Succeeded/Failed/Cancelled) when it ends.
        app.MapGet("/tasks/{id}/history", async (ICronnerClient client, string id, int take = 20) =>
            Results.Ok(await client.GetExecutionsAsync(id, take)));

        app.MapPost("/tasks/{id}/run", async (ICronnerClient client, string id) =>
        {
            try
            {
                await client.TriggerNowAsync(id);   // unambiguous "run now"
                return Results.Accepted($"/tasks/{id}", new { triggered = id });
            }
            catch (CronnerTaskNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Enqueue a one-off instance of the 'enqueue:notify' definition, carrying a typed payload.
        // The body is optional here: a bodyless POST falls back to a demo payload so the endpoint always
        // enqueues something valid (never a null payload, which would NRE inside the job).
        app.MapPost("/enqueue/notify", async (ICronnerClient client, NotificationPayload? payload) =>
        {
            payload ??= new NotificationPayload("ops@example.com", "hello from a one-off", 1);
            try
            {
                var instanceId = await client.EnqueueAsync("enqueue:notify", payload);
                return Results.Accepted($"/tasks/{instanceId}", new { enqueued = instanceId, payload });
            }
            catch (CronnerTaskNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapPost("/tasks/{id}/cancel", async (ICronnerClient client, string id) =>
        {
            try
            {
                await client.CancelTaskAsync(id);
                return Results.Accepted($"/tasks/{id}", new { cancelled = id });
            }
            catch (CronnerTaskNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapGet("/activity", (JobActivityLog activity, int take = 50, string? jobId = null) => Results.Ok(new
        {
            entries = activity.Recent(take, jobId),
            countsByJob = activity.CountsByJob(),
        }));

        app.MapGet("/metrics", (JobMetrics metrics) => Results.Ok(metrics.Snapshot()));

        app.MapGet("/progress", (JobProgressTracker progress) => Results.Ok(progress.Snapshot()));

        // Measured keepalive cadence per task, to check renewals really land every LockTtl/2 — even with
        // CRONNER_SLOW_KEEPALIVE_MS set longer than that interval.
        app.MapGet("/locks", (LockCadenceTracker cadence) => Results.Ok(cadence.Snapshot(options.LockTtl)));

        // Takes a running task's claim away: releases the lock on behalf of its current owner (the only way a
        // claim can change hands — a store never lets an Upsert overwrite lock fields). The next keepalive
        // renewal is then refused, the scheduler cancels its own run, and OnLockLost fires — i.e. this simulates
        // the claim lapsing and being reclaimed by another node.
        app.MapPost("/tasks/{id}/steal-lock", async (ICronnerStore store, string id) =>
        {
            var job = await store.GetByIdAsync(id);
            if (job is null)
                return Results.NotFound(new { error = $"No task with id '{id}'." });

            if (job.LockOwner is null)
                return Results.Conflict(new { error = $"Task '{id}' does not currently hold a lock — start it first." });

            var previousOwner = job.LockOwner;
            var released = await store.ReleaseLockAsync(id, previousOwner);
            return Results.Ok(new
            {
                releasedOnBehalfOf = previousOwner,
                released,
                note = "the owner's next keepalive is refused → its run is cancelled → OnLockLost; the task is claimable again",
            });
        });

        // The lease rule, live (custom JSON store only): make every lock renewal THROW for N seconds — the store
        // "cannot tell", like a database that is briefly unreachable. Start lock:outage first, then call this with
        // a short N (survives: renewals are retried while the last confirmed expiry is ahead) or a long one (the run
        // is abandoned BEFORE its lease lapses; OnLockLost fires; the history row ends Cancelled + LockLost).
        app.MapPost("/store/renewal-outage", (ICronnerStore store, int seconds = 15) =>
        {
            if (store is not JsonFileCronnerStore json)
                return Results.BadRequest(new { error = "The renewal-outage switch exists on the custom JSON store only (CRONNER_STORE=custom)." });

            json.RenewalOutageUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return Results.Ok(new
            {
                renewalsThrowUntil = json.RenewalOutageUntil,
                lockTtlSeconds = options.LockTtl.TotalSeconds,
                hint = seconds < options.LockTtl.TotalSeconds / 2
                    ? "short outage: the running task should survive (watch /activity for '[store] lock renewal THREW' followed by a successful keepalive)"
                    : "long outage: the running task is abandoned before its lease lapses (OnLockLost), then re-run",
            });
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        return app;
    }
}
