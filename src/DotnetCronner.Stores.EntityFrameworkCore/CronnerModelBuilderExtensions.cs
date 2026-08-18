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
            entity.Property(e => e.DefinitionId).HasMaxLength(256);
            entity.Property(e => e.PayloadType).HasMaxLength(512);
            entity.HasIndex(e => new { e.State, e.NextRunUtc });
            entity.HasIndex(e => e.NextRunUtc);
            // One-off lookups and retention pruning filter by definition + state.
            entity.HasIndex(e => new { e.DefinitionId, e.State });
        });

        modelBuilder.Entity<CronnerJobExecutionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskId).HasMaxLength(256);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.Owner).HasMaxLength(64);
            // Newest-first history reads and retention pruning filter by job and order by start time.
            entity.HasIndex(e => new { e.TaskId, e.StartedAt });
            // Finalizing a run looks the row up by its correlation id.
            entity.HasIndex(e => e.CorrelationId);
            // Tie each run to its job; deleting a job (incl. one-off retention pruning) removes its history.
            entity.HasOne<CronnerJobEntity>()
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}
