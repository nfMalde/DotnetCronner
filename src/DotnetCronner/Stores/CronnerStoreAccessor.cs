using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// Hands out the configured <see cref="ICronnerStore"/> for the duration of one operation.
///
/// <para>The scheduler and the client are singletons, so they cannot hold the store directly: under
/// <see cref="CronnerStoreLifetime.Scoped"/> that would capture one instance — and its DbContext,
/// ORM session or connection — for the process lifetime, and every overlapping scheduler operation
/// would share it. Instead each call opens a DI scope, resolves the store from it, and disposes the
/// scope when the operation completes.</para>
///
/// <para>Under <see cref="CronnerStoreLifetime.Singleton"/> the scope is still opened but the holder
/// returns the same cached instance, so the cost is one short-lived scope per call and behaviour is
/// unchanged.</para>
/// </summary>
public sealed class CronnerStoreAccessor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CronnerStoreHolder _holder;

    /// <summary>Creates the accessor.</summary>
    public CronnerStoreAccessor(IServiceScopeFactory scopes, CronnerStoreHolder holder)
    {
        _scopes = scopes;
        _holder = holder;
    }

    /// <summary>Runs <paramref name="operation"/> against a store valid for that call, and returns its result.</summary>
    public async Task<T> UseAsync<T>(Func<ICronnerStore, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var scope = _scopes.CreateScope();
        return await operation(_holder.Resolve(scope.ServiceProvider)).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="operation"/> against a store valid for that call.</summary>
    public async Task UseAsync(Func<ICronnerStore, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var scope = _scopes.CreateScope();
        await operation(_holder.Resolve(scope.ServiceProvider)).ConfigureAwait(false);
    }
}
