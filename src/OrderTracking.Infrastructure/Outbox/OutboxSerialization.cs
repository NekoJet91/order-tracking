using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// The single serializer configuration used for outbox payloads.
/// </summary>
/// <remarks>
/// Deliberately one shared instance. Statuses are written as names so a row is legible in psql and so
/// the wire format survives the enum being reordered.
/// </remarks>
internal static class OutboxSerialization
{
    /// <summary>camelCase members, enums as names.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
