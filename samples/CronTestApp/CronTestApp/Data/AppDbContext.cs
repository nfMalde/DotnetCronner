using DotnetCronner;
using Microsoft.EntityFrameworkCore;

namespace CronTestApp.Data;

/// <summary>
/// The "advanced" EF Core path from the migrations guide: an application-owned <see cref="DbContext"/>
/// that hosts the Cronner table itself. It must both implement <see cref="ICronnerDbContext"/> and call
/// <c>ApplyCronnerModel()</c> — selected with <c>CRONNER_STORE=ef-postgres-appcontext</c>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), ICronnerDbContext
{
    /// <inheritdoc />
    public DbSet<CronnerJobEntity> CronnerJobs => Set<CronnerJobEntity>();

    /// <inheritdoc />
    public DbSet<CronnerJobExecutionEntity> CronnerJobExecutions => Set<CronnerJobExecutionEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // REQUIRED: the interface only supplies the DbSet; this maps the table and its indexes.
        modelBuilder.ApplyCronnerModel();
    }
}
