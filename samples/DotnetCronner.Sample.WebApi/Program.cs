using DotnetCronner;
using DotnetCronner.Sample.WebApi.Jobs;

var builder = WebApplication.CreateBuilder(args);

// The job's dependencies are resolved from DI at execution time.
builder.Services.AddScoped<IGreeter, ConsoleGreeter>();

// Register DotnetCronner. Infrastructure (stores, cache) can be configured here or in app.UseDotnetCronner.
builder.Services.AddDotnetCronner(cronner => cronner
    .Configure(options => options.PollingInterval = TimeSpan.FromSeconds(1)));

var app = builder.Build();

// Schedule tasks (and, if desired, choose a store) after the host is built —
// this is the app.UseDotnetCronner(cronner => ...) surface from the design.
app.UseDotnetCronner(cronner => cronner
    // .UseRedisAsStore("localhost:6379")            // durable / distributed store (DotnetCronner.Stores.Redis)
    // .UseEntityFrameworkStore<AppDbContext>()      // EF Core store (DotnetCronner.Stores.EntityFrameworkCore)
    .Sched<SampleJobs>(
        x => x.SayHello("world", x.HasParam<CancellationToken>()),
        o => o.WithCron("*/5 * * * * *").WithConcurrency(CronnerConcurrencyMode.Queue)));

// No bundled dashboard — integrate management into your own (already secured) endpoints via ICronnerClient.
app.MapGet("/", () => "DotnetCronner sample. Try /tasks, /tasks/{id}, POST /tasks/{id}/run, POST /tasks/{id}/cancel");

app.MapGet("/tasks", async (ICronnerClient client, CronnerTaskState? state, int offset = 0, int limit = 50) =>
    Results.Ok(await client.GetTasksAsync(state, offset, limit)));

app.MapGet("/tasks/{id}", async (ICronnerClient client, string id) =>
    await client.GetTaskByIdAsync(id) is { } task ? Results.Ok(task) : Results.NotFound());

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
