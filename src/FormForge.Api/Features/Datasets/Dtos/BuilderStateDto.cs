using System.Text.Json;
using System.Text.Json.Serialization;

namespace FormForge.Api.Features.Datasets.Dtos;

// Story 11.1 (FR-70 / AR-66 / AR-67) — C# mirror of the canonical TypeScript
// BuilderState contract (web/src/features/datasets/types/builderState.ts). This is
// the server-side shape deserialized from custom_dataset.builder_state (JSONB) before
// DatasetSqlGenerator re-derives the SQL SELECT. Property names are PascalCase and
// bound to the camelCase JSON via BuilderStateSerializer.Options (PropertyNamingPolicy).
//
// The records mirror builderState.ts 1:1 (Decision 6.11): extend BOTH files together.

internal sealed record BuilderStateDto(
    List<TableNodeDto> Nodes,
    List<JoinEdgeDto> Edges,
    FilterGroupDto Filters,
    List<OrderByClauseDto> OrderBy,
    List<CaseColumnDto> CaseColumns,
    List<CalculatedColumnDto> CalculatedColumns);

internal sealed record TableNodeDto(
    string Id,
    TableNodeDataDto Data,
    NodePositionDto Position);

internal sealed record NodePositionDto(double X, double Y);

internal sealed record TableNodeDataDto(
    string TableName,
    string Side,           // "left" | "right"
    List<ColumnSelectionDto> Columns);

internal sealed record ColumnSelectionDto(
    string ColumnName,
    string PgType,
    bool Checked,
    string Aggregate,      // "none" | "COUNT" | "SUM" | "AVG" | "MIN" | "MAX"
    string Alias);

internal sealed record JoinEdgeDto(
    string Id,
    string Source,
    string Target,
    string SourceHandle,
    string TargetHandle,
    JoinEdgeDataDto Data);

internal sealed record JoinEdgeDataDto(string JoinType); // "INNER" | "LEFT" | "RIGHT" | "FULL OUTER"

// Discriminated union via the `kind` field; deserialized by FilterItemDtoConverter.
[JsonConverter(typeof(FilterItemDtoConverter))]
internal abstract record FilterItemDto(string Kind);

internal sealed record FilterGroupDto(
    string Id,
    string Kind,           // "group"
    string Combinator,     // "AND" | "OR"
    List<FilterItemDto> Items) : FilterItemDto(Kind);

internal sealed record FilterConditionDto(
    string Id,
    string Kind,           // "condition"
    string TableName,
    string ColumnName,
    string Operator,       // "=" | "!=" | "<" | "<=" | ">" | ">=" | "IS NULL"
                           // | "IS NOT NULL" | "LIKE" | "ILIKE" | "IN" | "NOT IN" | "BETWEEN"
    JsonElement Value      // string | string[] | null — inspected at generation time
) : FilterItemDto(Kind);

internal sealed record OrderByClauseDto(
    string TableName,
    string ColumnName,
    string Direction);     // "ASC" | "DESC"

internal sealed record CaseColumnDto(
    string Id,
    string NodeId,
    string Alias,
    List<CaseWhenDto> Whens,
    string ElseValue);

internal sealed record CaseWhenDto(
    string NodeId,
    string ColumnName,
    string Operator,
    string OperandValue,
    string ThenValue);

internal sealed record CalculatedColumnDto(
    string Id,
    string NodeId,
    string Alias,
    string Expression);

// The `items` array in a FilterGroupDto holds a mix of FilterGroupDto and
// FilterConditionDto. System.Text.Json cannot resolve this polymorphism without a
// custom converter, so we peek at the `kind` field before dispatching to the concrete
// record. Registered both via [JsonConverter] on FilterItemDto and in
// BuilderStateSerializer.Options.Converters (see Dev Notes §4).
internal sealed class FilterItemDtoConverter : JsonConverter<FilterItemDto>
{
    public override FilterItemDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var kind = doc.RootElement.TryGetProperty("kind", out var kindEl)
            ? kindEl.GetString()
            : null;
        var raw = doc.RootElement.GetRawText();
        return kind == "group"
            ? JsonSerializer.Deserialize<FilterGroupDto>(raw, options)
            : JsonSerializer.Deserialize<FilterConditionDto>(raw, options);
    }

    public override void Write(Utf8JsonWriter writer, FilterItemDto value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

// Centralized (de)serialization options for builder_state. The FilterItemDtoConverter
// is registered in the field initializer so it is always present before the first
// Deserialize call (Dev Notes §4). Deserialize swallows malformed JSON and returns
// null so DatasetService can map it to BUILDER_STATE_INVALID (422) rather than 500.
internal static class BuilderStateSerializer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new FilterItemDtoConverter() },
    };

    internal static BuilderStateDto? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BuilderStateDto>(json, Options);
        }
#pragma warning disable CA1031 // any malformed blob → null → BUILDER_STATE_INVALID, never a 500
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
