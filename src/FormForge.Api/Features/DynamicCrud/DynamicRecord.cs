using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FormForge.Api.Features.DynamicCrud;

// Story 6.1 — one row from a provisioned dynamic table. Wraps the raw Dapper
// DapperRow (IDictionary<string,object>) as IReadOnlyDictionary so the JSON
// converter can iterate the columns without depending on Dapper at the JSON
// layer. The converter implements AR-46 Option C hybrid serialization: system
// column PG names are renamed to camelCase; user fieldKeys pass through
// verbatim because they are already validated SQL identifiers.
internal sealed class DynamicRecord
{
    public IReadOnlyDictionary<string, object?> Values { get; }

    internal DynamicRecord(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
    }
}

// AR-46 Option C — system columns are renamed to camelCase JSON keys; everything
// else (user fieldKeys validated by SafeIdentifier at provisioning) is written
// verbatim. The converter is responsible only for property NAMES; value
// serialization is delegated back to the configured JsonSerializerOptions so
// Guid/DateTime/DateTimeOffset/bool/decimal/string/null land in the same shape
// the rest of the API uses.
internal sealed class DynamicRecordJsonConverter : JsonConverter<DynamicRecord>
{
    private static readonly FrozenDictionary<string, string> SystemColumnJsonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "id",
            ["created_at"] = "createdAt",
            ["created_by"] = "createdBy",
            ["updated_at"] = "updatedAt",
            ["updated_by"] = "updatedBy",
            ["is_deleted"] = "isDeleted",
            ["cascade_event_id"] = "cascadeEventId",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public override DynamicRecord? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("DynamicRecord is response-only; deserialization is not supported.");

    public override void Write(Utf8JsonWriter writer, DynamicRecord value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        foreach (var (key, raw) in value.Values)
        {
            var jsonName = SystemColumnJsonNames.TryGetValue(key, out var camel) ? camel : key;
            writer.WritePropertyName(jsonName);
            JsonSerializer.Serialize(writer, raw, options);
        }
        writer.WriteEndObject();
    }
}
