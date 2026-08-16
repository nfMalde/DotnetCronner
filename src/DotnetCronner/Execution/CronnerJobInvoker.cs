using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>Resolves a task's target instance and arguments from a scope and invokes it.</summary>
internal static class CronnerJobInvoker
{
    public static Task InvokeAsync(
        IServiceProvider services, CronnerJobDescriptor descriptor, CancellationToken cancellationToken) =>
        InvokeAsync(services, descriptor.TargetType, descriptor.Method, descriptor.Arguments, cancellationToken);

    public static async Task InvokeAsync(
        IServiceProvider services, Type targetType, MethodInfo method,
        IReadOnlyList<CronnerArgument> argumentPlan, CancellationToken cancellationToken)
    {
        object? target = method.IsStatic
            ? null
            : ActivatorUtilities.GetServiceOrCreateInstance(services, targetType);

        var arguments = new object?[argumentPlan.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = argumentPlan[i];
            arguments[i] = argument.Kind switch
            {
                CronnerArgumentKind.CancellationToken => cancellationToken,
                CronnerArgumentKind.Service => services.GetRequiredService(argument.Type),
                CronnerArgumentKind.Literal => argument.Value,
                CronnerArgumentKind.Factory => argument.Factory!(services),
                _ => null,
            };
        }

        object? result;
        try
        {
            result = method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real exception (from a synchronous throw) rather than the reflection wrapper.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }

        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;
        }
    }
}
