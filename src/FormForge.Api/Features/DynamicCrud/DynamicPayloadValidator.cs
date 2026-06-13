using System.Globalization;
using System.Text.Json;
using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Features.DynamicCrud;

// Story 6.3 — Layer 2 dynamic payload validator (AR-20 + Decision 3.3). Iterates
// over the user-authored ColumnDefinitions only; unknown keys in the payload are
// silently dropped (AC-2), system column names like "id" / "created_at" are never
// matched because they are not in ColumnDefinition entries.
internal interface IDynamicPayloadValidator
{
    PayloadValidationResult Validate(
        JsonElement body,
        IReadOnlyList<ColumnDefinition> columns);
}

internal sealed record PayloadValidationResult(
    bool IsValid,
    IReadOnlyDictionary<string, object?> CoercedValues,
    IDictionary<string, string[]> FieldErrors);

internal sealed class DynamicPayloadValidator : IDynamicPayloadValidator
{
    public PayloadValidationResult Validate(JsonElement body, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var coerced = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors  = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (body.ValueKind != JsonValueKind.Object)
        {
            return new PayloadValidationResult(false, coerced,
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["$"] = ["Request body must be a JSON object."],
                });
        }

        foreach (var col in columns)
        {
            if (!body.TryGetProperty(col.ColumnName, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Null)
            {
                coerced[col.ColumnName] = null;
                continue;
            }
            // Story B — coerce by behaviour family (PgTypeInfo) so authored types
            // (varchar(n), integer, date, …) validate correctly while legacy
            // constants (TEXT/NUMERIC/BOOLEAN/TIMESTAMPTZ/UUID/JSONB) keep working
            // via the case-insensitive resolver. baseType drives the fine-grained
            // numeric/temporal coercion within a family.
            var baseType = PgTypeInfo.BaseOf(col.PgType);
            switch (PgTypeInfo.FamilyOf(col.PgType))
            {
                case PgTypeFamily.Text:
                    if (el.ValueKind != JsonValueKind.String)
                        errors[col.ColumnName] = [$"Expected a string for field '{col.ColumnName}'."];
                    else
                        coerced[col.ColumnName] = el.GetString();
                    break;
                case PgTypeFamily.Numeric:
                    CoerceNumeric(el, col.ColumnName, baseType, coerced, errors);
                    break;
                case PgTypeFamily.Boolean:
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        coerced[col.ColumnName] = el.GetBoolean();
                    else
                        errors[col.ColumnName] = [$"Expected a boolean for field '{col.ColumnName}'."];
                    break;
                case PgTypeFamily.Temporal:
                    CoerceTemporal(el, col.ColumnName, baseType, coerced, errors);
                    break;
                case PgTypeFamily.Uuid:
                    // Designer-backed Dropdown stores the referenced row's UUID id.
                    // The value arrives as a JSON string; parse it to a Guid so Npgsql
                    // binds it as uuid (a raw string would yield PG 42804). An empty /
                    // whitespace string is a cleared selection → SQL NULL.
                    if (el.ValueKind != JsonValueKind.String)
                        errors[col.ColumnName] = [$"Expected a UUID string for field '{col.ColumnName}'."];
                    else
                    {
                        var rawUuid = el.GetString();
                        if (string.IsNullOrWhiteSpace(rawUuid))
                            coerced[col.ColumnName] = null;
                        else if (Guid.TryParse(rawUuid, CultureInfo.InvariantCulture, out var guid))
                            coerced[col.ColumnName] = guid;
                        else
                            errors[col.ColumnName] = [$"Expected a valid UUID for field '{col.ColumnName}'."];
                    }
                    break;
                case PgTypeFamily.Json:
                default:
                    // jsonb — keep the raw JSON text (GetRawText preserves objects,
                    // arrays, numbers, and quoted strings as valid JSON). The insert/
                    // update builder binds it with an explicit ::jsonb cast.
                    coerced[col.ColumnName] = el.GetRawText();
                    break;
            }
        }

        return new PayloadValidationResult(errors.Count == 0, coerced, errors);
    }

    // Story B — numeric family coercion. Integer types bind as long (and reject
    // fractional input so PG doesn't 22P02); float types bind as double; exact
    // numeric/decimal binds as decimal. Accepts JSON numbers and numeric strings.
    private static void CoerceNumeric(
        JsonElement el, string col, string baseType,
        Dictionary<string, object?> coerced, Dictionary<string, string[]> errors)
    {
        if (PgTypeInfo.IsIntegerBase(baseType))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var i))
                coerced[col] = i;
            else if (el.ValueKind == JsonValueKind.String &&
                     long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var si))
                coerced[col] = si;
            else
                errors[col] = [$"Expected a whole number for field '{col}'."];
            return;
        }
        if (PgTypeInfo.IsFloatBase(baseType))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
                coerced[col] = d;
            else if (el.ValueKind == JsonValueKind.String &&
                     double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd))
                coerced[col] = sd;
            else
                errors[col] = [$"Expected a number for field '{col}'."];
            return;
        }
        // numeric / decimal — exact. TryGetDecimal returns false (not throws) for
        // JSON numbers outside decimal range (e.g. 1e40) → 422 rather than 500.
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var dec))
            coerced[col] = dec;
        else if (el.ValueKind == JsonValueKind.String &&
                 decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var sdec))
            coerced[col] = sdec;
        else
            errors[col] = [$"Expected a number for field '{col}'."];
    }

    // Story B — temporal family coercion. Each base binds the CLR type Npgsql maps
    // to the matching PG type: date→DateOnly, time→TimeOnly, timestamp (without tz)
    // →DateTime(Unspecified), timestamptz→DateTimeOffset(UTC). AssumeUniversal +
    // AdjustToUniversal so a timezone-less timestamptz input lands as UTC, not
    // server-local, making identical payloads from different zones the same row.
    private static void CoerceTemporal(
        JsonElement el, string col, string baseType,
        Dictionary<string, object?> coerced, Dictionary<string, string[]> errors)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            errors[col] = [$"Expected a date/time string for field '{col}'."];
            return;
        }
        var raw = el.GetString();
        switch (baseType)
        {
            case "date":
                if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    coerced[col] = d;
                else
                    errors[col] = [$"Expected an ISO-8601 date (YYYY-MM-DD) for field '{col}'."];
                break;
            case "time":
                if (TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    coerced[col] = t;
                else
                    errors[col] = [$"Expected a time (HH:MM[:SS]) for field '{col}'."];
                break;
            case "timestamp":
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    coerced[col] = DateTime.SpecifyKind(ts, DateTimeKind.Unspecified);
                else
                    errors[col] = [$"Expected an ISO-8601 timestamp for field '{col}'."];
                break;
            default:   // timestamptz (and legacy "TIMESTAMPTZ")
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    coerced[col] = dto;
                else
                    errors[col] = [$"Expected an ISO-8601 timestamp string for field '{col}'."];
                break;
        }
    }
}
