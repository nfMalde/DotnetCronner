using Microsoft.EntityFrameworkCore;

namespace DotnetCronner;

/// <summary>Model configuration helpers for the DotnetCronner EF Core store.</summary>
public static class CronnerModelBuilderExtensions
{
    /// <summary>
    /// Configures the <see cref="CronnerJobEntity"/> mapping in a provider-agnostic way. Call this from
    /// your <c>DbContext.OnModelCreating(ModelBuilder)</c>. Customize the table name or other relational
    /// details separately if needed.
    /// </summary>
    public static ModelBuilder ApplyCronnerModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<CronnerJobEntity>(entity =>
        {
            entity.HasKey(e => e.TaskId);
            entity.Property(e => e.TaskId).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(512);
            entity.HasIndex(e => new { e.State, e.NextRunUtc });
            entity.HasIndex(e => e.NextRunUtc);
        });

        return modelBuilder;
    }
}
