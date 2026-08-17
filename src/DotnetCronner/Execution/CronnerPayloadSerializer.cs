using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCronner;

/// <summary>
/// Serializes one-off job payloads to JSON for storage and back. Serialization always uses the caller's
/// <em>declared</em> type, so a lazy-loading ORM proxy (Castle / NHibernate / EF) only emits that type's
/// shape rather than proxy internals or navigation graphs; <see cref="ReferenceHandler.IgnoreCycles"/> and
/// a bounded <see cref="JsonSerializerOptions.MaxDepth"/> defuse the infinite-loop trap on cyclic graphs.
/// Payloads should still be plain, serializable DTOs.
/// </summary>
internal static class CronnerPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 64,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes <paramref name="payload"/> as its declared <paramref name="declaredType"/>.</summary>
    public static string Serialize(object? payload, Type declaredType) =>
        JsonSerializer.Serialize(payload, declaredType, Options);

    /// <summary>Deserializes <paramref name="json"/> to <paramref name="targetType"/>.</summary>
    public static object? Deserialize(string json, Type targetType) =>
        JsonSerializer.Deserialize(json, targetType, Options);
}
