using CronTestApp.Configuration;
using CronTestApp.Endpoints;

// Load .env before the host is built: docker compose reads the same file for its own services, and this
// makes `dotnet run` / F5 behave identically. Real environment variables always win.
var envFile = DotEnvFile.Load();

var builder = WebApplication.CreateBuilder(args);

// Everything about this app is steered by .env — parse it once, then hand it to the wiring.
var options = TestAppOptions.FromConfiguration(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddTestAppCronner(options);

var app = builder.Build();

app.Logger.LogInformation("Loaded environment file: {EnvFile}", envFile ?? "(none — using process environment)");
app.Logger.LogInformation(
    "DotnetCronner test harness: store={Store}, cache={Cache}, discovery={Discovery}, jobServices={JobServices}, timeZone={TimeZone}",
    options.Store, options.Cache, options.Discovery, options.JobServices, options.TimeZone.Id);

// Create the schema first when running on EF Core, so the scheduler never starts against a missing table.
await app.PrepareStoreAsync(options);

// Post-build configuration: the lambda tasks and the fluent lifecycle hooks.
app.UseTestAppCronner(options);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapCronnerTestEndpoints(options);

app.Run();
