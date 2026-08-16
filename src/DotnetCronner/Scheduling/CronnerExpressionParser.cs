using System.Linq.Expressions;
using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// Turns a <c>Sched</c> lambda such as <c>x =&gt; x.DoWork(x.HasParam&lt;IFoo&gt;(), 42)</c> into the
/// target <see cref="MethodInfo"/> and an argument plan.
/// </summary>
internal static class CronnerExpressionParser
{
    public static (MethodInfo Method, IReadOnlyList<CronnerArgument> Arguments) Parse(LambdaExpression expression)
    {
        if (expression.Body is not MethodCallExpression call)
            throw new ArgumentException(
                "A scheduling expression must be a single method call, e.g. x => x.DoWork(...).",
                nameof(expression));

        var arguments = new CronnerArgument[call.Arguments.Count];
        for (var i = 0; i < call.Arguments.Count; i++)
            arguments[i] = ParseArgument(call.Arguments[i]);

        return (call.Method, arguments);
    }

    private static CronnerArgument ParseArgument(Expression argument)
    {
        if (IsHasParamCall(argument, out var call))
        {
            var providedType = call.Method.GetGenericArguments()[0];

            // Factory overload: HasParam<T>(instance, sp => ...). Args are [instance, factory].
            if (call.Arguments.Count == 2)
                return CronnerArgument.FromFactory(providedType, CompileFactory(call.Arguments[1]));

            return providedType == typeof(CancellationToken)
                ? CronnerArgument.CancellationToken()
                : CronnerArgument.Service(providedType);
        }

        // Anything else is a fixed value captured from the expression.
        var value = Expression.Lambda(Expression.Convert(argument, typeof(object))).Compile().DynamicInvoke();
        return CronnerArgument.Literal(argument.Type, value);
    }

    private static Func<IServiceProvider, object?> CompileFactory(Expression factoryExpression)
    {
        // Turn the user's Func<IServiceProvider, T> expression into Func<IServiceProvider, object?>.
        var provider = Expression.Parameter(typeof(IServiceProvider), "sp");
        var invoke = Expression.Invoke(factoryExpression, provider);
        var body = Expression.Convert(invoke, typeof(object));
        return Expression.Lambda<Func<IServiceProvider, object?>>(body, provider).Compile();
    }

    private static bool IsHasParamCall(Expression argument, out MethodCallExpression call)
    {
        if (argument is MethodCallExpression candidate &&
            candidate.Method.IsGenericMethod &&
            candidate.Method.DeclaringType == typeof(CronnerExpressions) &&
            candidate.Method.Name == nameof(CronnerExpressions.HasParam))
        {
            call = candidate;
            return true;
        }

        call = null!;
        return false;
    }
}
