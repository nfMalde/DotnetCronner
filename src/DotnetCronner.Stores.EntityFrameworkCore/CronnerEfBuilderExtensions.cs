using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>Fluent extensions for using Entity Framework Core as the DotnetCronner store.</summary>
public static class CronnerEfBuilderExtensions
{
    /// <summary>
    /// Uses EF Core as the backing store via a <see cref="DbContext"/> that implements
    /// <see cref="ICronnerDbContext"/>. Register the context with
    /// <c>services.AddDbContextFactory&lt;TContext&gt;(...)</c> and configure the model with
    /// <see cref="CronnerModelBuilderExtensions.ApplyCronnerModel"/>. Mutually exclusive with other store providers.
    /// </summary>
    public static ICronnerBuilder UseEntityFrameworkStore<TContext>(this ICronnerBuilder builder)
        where TContext : DbContext, ICronnerDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.StoreHolder.ConfigureStore(
            sp => new EfCronnerStore<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>()),
            $"UseEntityFrameworkStore<{typeof(TContext).Name}>()");
        return builder;
    }

    /// <summary>
    /// Uses EF Core as the backing store via the built-in <see cref="CronnerDbContext"/> — no custom
    /// context and no <see cref="CronnerModelBuilderExtensions.ApplyCronnerModel"/> call to remember.
    /// Registers the context factory with the given provider configuration.
    /// </summary>
    /// <remarks>
    /// Use this inside <c>services.AddDotnetCronner(...)</c> (registration time), for example:
    /// <code>
    /// builder.Services.AddDotnetCronner(c => c.UseEntityFrameworkStore(o => o.UseNpgsql(connectionString)));
    /// </code>
    /// </remarks>
    public static ICronnerBuilder UseEntityFrameworkStore(
        this ICronnerBuilder builder, Action<DbContextOptionsBuilder> configureDbContext)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureDbContext);
        builder.Services.AddDbContextFactory<CronnerDbContext>(configureDbContext);
        return builder.UseEntityFrameworkStore<CronnerDbContext>();
    }
}
