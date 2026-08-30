using DotnetCronner;
using DotnetCronner.Sample.WebApi.Jobs;

var builder = WebApplication.CreateBuilder(args);

// The job's dependencies are resolved from DI at execution time.
builder.Services.AddScoped<IGreeter, ConsoleGreeter>();

// Collects progress purely from the hooks, for GET /progress.
builder.Services.AddSingleton<ProgressLog>();

// The app's OWN per-run summary store, correlated by ExecutionId — the recommended alternative to attaching
// application data to the execution-history row. Served from GET /runs.
builder.Services.AddSingleton<RunSummaryStore>();

// Register DotnetCronner. Infrastructure (stores, cache) can be configured here or in app.UseDotnetCronner.
builder.Services.AddDotnetCronner(cronner => cronner
    .Configure(options => options.PollingInterval = TimeSpan.FromSeconds(1))
    // Record the newest 20 runs per task (off by default); read them via GET /tasks/{id}/history.
    .WithExecutionHistory(20)
    // A global hook that turns total/scope progress (and each report's custom payload) into GET /progress.
    .AddHook<ProgressHook>());

var app = builder.Build();

// Schedule tasks (and, if desired, choose a store) after the host is built —
// this is the app.UseDotnetCronner(cronner => ...) surface from the design.
app.UseDotnetCronner(cronner => cronner
    // .UseRedisAsStore("localhost:6379")            // durable / distributed store (DotnetCronner.Stores.Redis)
    // .UseEntityFrameworkStore<AppDbContext>()      // EF Core store (DotnetCronner.Stores.EntityFrameworkCore)
    .Sched<SampleJobs>(
        x => x.SayHello("world", x.HasParam<CancellationToken>()),
        o => o.WithCron("*/5 * * * * *").WithConcurrency(CronnerConcurrencyMode.Queue))
    // A progress job: reports total + scope progress, each report carrying a custom payload. See GET /progress.
    .Sched<ImportJob>(
        x => x.RunAsync(x.HasParam<ICronnerJobContext>(), x.HasParam<CancellationToken>()),
        o => o.WithCron("*/20 * * * * *").WithId("import")));

// No bundled dashboard — integrate management into your own (already secured) endpoints via ICronnerClient.
app.MapGet("/", () => "DotnetCronner sample. Try /tasks, /tasks/{id}, /tasks/{id}/history, /progress, /runs, POST /tasks/{id}/run, POST /tasks/{id}/cancel");

// Live progress rebuilt from the progress hooks, incl. each report's custom payload (`note`).
app.MapGet("/progress", (ProgressLog log) => Results.Ok(log.Snapshot()));

// The app's own per-run summaries, correlated to executions by ExecutionId (the recommended pattern in place
// of attaching application data to the execution-history row).
app.MapGet("/runs", (RunSummaryStore summaries) => Results.Ok(summaries.Snapshot()));

app.MapGet("/tasks", async (ICronnerClient client, CronnerTaskState? state, int offset = 0, int limit = 50) =>
    Results.Ok(await client.GetTasksAsync(state, offset, limit)));

app.MapGet("/tasks/{id}", async (ICronnerClient client, string id) =>
    await client.GetTaskByIdAsync(id) is { } task ? Results.Ok(task) : Results.NotFound());

// Execution history for a task, newest first (recorded because WithExecutionHistory is enabled above). Each row's
// Id is the ExecutionId the job and its hooks saw; the app's own per-run summary for that id is at GET /runs.
app.MapGet("/tasks/{id}/history", async (ICronnerClient client, string id, int take = 20) =>
    Results.Ok(await client.GetExecutionsAsync(id, take)));

app.MapPost("/tasks/{id}/run", async (ICronnerClient client, string id) =>
{
    await client.ScheduleTaskAsync(id);
    return Results.Accepted($"/tasks/{id}");
});

app.MapPost("/tasks/{id}/cancel", async (ICronnerClient client, string id) =>
{
    await client.CancelTaskAsync(id);
    return Results.Accepted($"/tasks/{id}");
});

app.Run();
