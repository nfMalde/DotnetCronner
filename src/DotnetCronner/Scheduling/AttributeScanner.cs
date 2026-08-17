using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// Discovers <see cref="CronnerTaskAttribute"/>-annotated methods in the configured assemblies and
/// builds descriptors for them. All parameters are resolved from DI, with
/// <see cref="CancellationToken"/> parameters bound to the task's cancellation token.
/// </summary>
internal static class AttributeScanner
{
    private const BindingFlags MethodFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static IEnumerable<CronnerJobDescriptor> Scan(
        IEnumerable<Assembly> assemblies, IReadOnlyCollection<Type> typeFilters)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                // When type filters are configured, only consider types assignable to one of them.
                if (typeFilters.Count > 0 && !typeFilters.Any(f => f.IsAssignableFrom(type)))
                    continue;

                foreach (var method in type.GetMethods(MethodFlags))
                {
                    var attribute = method.GetCustomAttribute<CronnerTaskAttribute>();
                    if (attribute is null)
                        continue;

                    var arguments = method.GetParameters()
                        .Select(p => p.ParameterType == typeof(CancellationToken)
                            ? CronnerArgument.CancellationToken()
                            : CronnerArgument.Service(p.ParameterType))
                        .ToArray();

                    var id = string.IsNullOrWhiteSpace(attribute.Id)
                        ? CronnerJobNaming.GetDefaultId(method)
                        : attribute.Id;

                    CronnerCronGuard.Validate(id, attribute.CronString);

                    yield return new CronnerJobDescriptor(
                        id, CronnerJobNaming.GetName(method), attribute.CronString, type, method, arguments,
                        attribute.Priority, attribute.Concurrency, attribute.Description);
                }
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
