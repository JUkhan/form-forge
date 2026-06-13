using System.Text.Json;

namespace FormForge.Api.Features.SchemaRegistry;

// Story B — the first authored pgType in a Designer RootElement that is not a
// supported, DDL-safe PostgreSQL type. FieldKey is "" when the offending element
// has no fieldKey yet.
internal sealed record PgTypeValidationError(string FieldKey, string PgType, string Message);

// Story B — validates the authored "pgType" property on every element of a
// Designer RootElement. Unlike RootElementParser (which silently falls back to a
// default on a malformed pgType so legacy designs keep provisioning), this walks
// the tree specifically to REPORT a bad pgType so the bind endpoint can return a
// 422 instead of silently dropping the author's intent at provision time.
//
// A missing pgType is allowed (absence → component-default fallback). Only a
// present-but-malformed value is an error.
internal static class DesignerPgTypeValidator
{
    private const int MaxDepth = 64;

    public static PgTypeValidationError? FindFirstInvalid(string? rootElementJson)
    {
        if (string.IsNullOrWhiteSpace(rootElementJson)) return null;

        using var doc = JsonDocument.Parse(rootElementJson);
        return Walk(doc.RootElement, 0);
    }

    private static PgTypeValidationError? Walk(JsonElement element, int depth)
    {
        if (depth > MaxDepth) return null;
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("pgType", out var pgTypeProp) &&
            pgTypeProp.ValueKind == JsonValueKind.String)
        {
            var pgType = pgTypeProp.GetString();
            // Absent / blank pgType is allowed (falls back to the component default).
            if (!string.IsNullOrWhiteSpace(pgType) &&
                !SafePgType.TryCreate(pgType, out _, out var error))
            {
                var fieldKey = properties.TryGetProperty("fieldKey", out var fk) &&
                               fk.ValueKind == JsonValueKind.String
                    ? fk.GetString() ?? string.Empty
                    : string.Empty;
                return new PgTypeValidationError(fieldKey, pgType!, error ?? "Unsupported PostgreSQL type.");
            }
        }

        if (element.TryGetProperty("children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                var result = Walk(child, depth + 1);
                if (result is not null) return result;
            }
        }

        return null;
    }
}
