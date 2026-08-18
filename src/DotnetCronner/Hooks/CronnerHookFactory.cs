using System.Linq.Expressions;

namespace DotnetCronner;

/// <summary>Builds <see cref="ICronnerTaskHook"/> instances for the fluent <c>On*</c> / <c>WithHook</c> helpers.</summary>
internal static class CronnerHookFactory
{
    public static ICronnerTaskHook FromDelegate(CronnerHookEvent hookEvent, Func<CronnerTaskContext, Task> handler) =>
        new DelegateCronnerTaskHook(hookEvent, handler);

    public static ICronnerTaskHook FromExpression(CronnerHookEvent hookEvent, Type targetType, LambdaExpression call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var (method, arguments) = CronnerExpressionParser.Parse(call);
        return new ExpressionCronnerTaskHook(hookEvent, targetType, method, arguments);
    }

    public static ICronnerTaskHook FromType(Type hookType, CronnerHookScope? scope = null) =>
        new ResolvedCronnerTaskHook(hookType, scope);

    /// <summary>Attaches a scope preference to a caller-supplied hook instance (no-op when <paramref name="scope"/> is null).</summary>
    public static ICronnerTaskHook WithScope(ICronnerTaskHook hook, CronnerHookScope? scope) =>
        scope is { } value ? new ScopedHookWrapper(hook, value) : hook;
}
