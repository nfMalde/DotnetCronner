using CronTestApp.Data;
using DotnetCronner;
using Microsoft.EntityFrameworkCore;

namespace CronTestApp.Configuration;

/// <summary>
/// Prepares whatever the selected store needs before the scheduler starts — for the EF Core stores that
/// means creating the <c>CronnerJobs</c> table.
/// </summary>
public static class StoreBootstrap
{
    /// <summary>
    /// Creates the schema for the EF Core stores, retrying while the database container is still coming up.
    /// </summary>
    /// <remarks>
    /// This uses <c>EnsureCreatedAsync()</c>, which is the throwaway-database shortcut: it creates the
    /// schema without migrations. Real applications generate migrations and call <c>MigrateAsync()</c>
    /// instead — see MIGRATIONS.md in the EF Core store package.
    /// </remarks>
    public static async Task PrepareStoreAsync(this IHost host, TestAppOptions options)
    {
        if (!options.UsesPostgres)
            return;

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cronner.Store");

        await WithRetriesAsync(logger, async () =>
        {
            await using var context = options.Store switch
            {
                StoreKind.EfPostgres => (DbContext)await host.Services
                    .GetRequiredService<IDbContextFactory<CronnerDbContext>>().CreateDbContextAsync(),
                StoreKind.EfPostgresAppContext => await host.Services
                    .GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(),
                _ => throw new InvalidOperationException($"Store '{options.Store}' does not use EF Core."),
            };

            await context.Database.EnsureCreatedAsync();
            logger.LogInformation(
                "PostgreSQL schema ready for {Context} (EnsureCreated).", context.GetType().Name);
        });
    }

    private static async Task WithRetriesAsync(ILogger logger, Func<Task> action, int attempts = 15)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                logger.LogWarning(
                    "Database not ready yet (attempt {Attempt}/{Attempts}): {Error}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
