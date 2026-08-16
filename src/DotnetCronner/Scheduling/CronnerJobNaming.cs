using System.Reflection;

namespace DotnetCronner;

/// <summary>Helpers for deriving a task's name and default id from its method.</summary>
internal static class CronnerJobNaming
{
    /// <summary>The human readable, fully qualified <c>Namespace.Type.Method</c> name.</summary>
    public static string GetName(MethodInfo method)
    {
        var typeName = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "UnknownType";
        return $"{typeName}.{method.Name}";
    }

    /// <summary>The deterministic default id used when none is supplied — the fully qualified name.</summary>
    public static string GetDefaultId(MethodInfo method) => GetName(method);
}
