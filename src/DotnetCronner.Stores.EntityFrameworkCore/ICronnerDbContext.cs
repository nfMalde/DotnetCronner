using Microsoft.EntityFrameworkCore;

namespace DotnetCronner;

/// <summary>
/// Marker interface a consumer's <see cref="DbContext"/> implements to expose the DotnetCronner tables.
/// Call <see cref="CronnerModelBuilderExtensions.ApplyCronnerModel"/> from <c>OnModelCreating</c>.
/// </summary>
public interface ICronnerDbContext
{
    /// <summary>The scheduled tasks table.</summary>
    DbSet<CronnerJobEntity> CronnerJobs { get; }
}
