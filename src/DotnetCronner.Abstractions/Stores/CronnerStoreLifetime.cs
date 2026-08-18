namespace DotnetCronner;

/// <summary>
/// How often DotnetCronner builds the configured <see cref="ICronnerStore"/>.
///
/// <para>This is deliberately explicit rather than inferred from a DI registration. The store is
/// consumed by the scheduler, which is a singleton, so a DI lifetime alone cannot express it: a
/// scoped registration resolved from the root provider silently becomes captive, and nothing about
/// that is visible until concurrent operations collide at runtime.</para>
/// </summary>
public enum CronnerStoreLifetime
{
    /// <summary>
    /// One instance for the application's lifetime.
    ///
    /// <para>Correct for a store that is either stateless or owns its own state. The built-in
    /// in-memory store REQUIRES this — it <i>is</i> the state, so rebuilding it would discard every
    /// job. Redis is safe here because the multiplexer is thread-safe, and the EF store because it
    /// holds an <c>IDbContextFactory</c> and creates a context per call.</para>
    ///
    /// <para>A singleton store must be safe for concurrent use: the scheduler polls while jobs run,
    /// so its methods overlap.</para>
    /// </summary>
    Singleton = 0,

    /// <summary>
    /// A new instance per scheduler operation, built from that operation's scope.
    ///
    /// <para>Correct for a store that holds a scoped, non-concurrent resource — an EF
    /// <c>DbContext</c>, an NHibernate session, an open connection. Each operation gets its own, so
    /// overlapping scheduler work cannot share one and produce "a command is already in progress"
    /// (Npgsql) or "a second operation was started on this context instance" (EF Core).</para>
    /// </summary>
    Scoped = 1,
}
