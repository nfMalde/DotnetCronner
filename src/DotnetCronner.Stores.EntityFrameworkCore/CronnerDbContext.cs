using Microsoft.EntityFrameworkCore;

namespace DotnetCronner;

/// <summary>
/// A ready-to-use <see cref="DbContext"/> that already maps the DotnetCronner tables. Use this when you
/// do not want to add the mapping to your own context — it calls
/// <see cref="CronnerModelBuilderExtensions.ApplyCronnerModel"/> itself, so there is nothing to remember
/// to wire up (unlike implementing <see cref="ICronnerDbContext"/> by hand).
/// </summary>
public class CronnerDbContext : DbContext, ICronnerDbContext
{
    /// <summary>Creates the context.</summary>
    public CronnerDbContext(DbContextOptions<CronnerDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    public DbSet<CronnerJobEntity> CronnerJobs => Set<CronnerJobEntity>();

    /// <inheritdoc />
    public DbSet<CronnerJobExecutionEntity> CronnerJobExecutions => Set<CronnerJobExecutionEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyCronnerModel();
    }
}
