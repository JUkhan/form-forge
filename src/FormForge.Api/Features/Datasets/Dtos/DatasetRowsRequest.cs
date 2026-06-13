namespace FormForge.Api.Features.Datasets.Dtos;

// Request bodies for the DatasetComponent runtime data-view endpoints
// (POST /api/datasets/{id}/rows and /rows/export). The filter tree reuses the
// canonical FilterGroupDto (same shape the Query Builder persists), so the runtime
// FilterGroup the SPA resolves from its fieldKey-bound conditions binds directly.

internal sealed record DatasetSortDto(string Column, string Direction); // Direction: "asc" | "desc"

internal sealed record DatasetRowsRequest(
    FilterGroupDto? Filters = null,
    List<DatasetSortDto>? Sort = null,
    List<string>? Columns = null,   // optional column allow/order list; null/empty ⇒ all columns
    int Page = 1,
    int PageSize = 25,
    // Optional auth filter column (the DatasetComponent's authFilterColumn property).
    // When set, the rows are scoped to those whose value equals the requesting user's
    // id. The COLUMN comes from the client (schema config); the VALUE is always the
    // server-resolved JWT user id (never client-supplied), so it can't be spoofed.
    string? AuthFilterColumn = null);

// One export column: the source column plus an optional header override (defaults to the column name).
internal sealed record DatasetExportColumnDto(string Column, string? Header = null);

internal sealed record DatasetExportRequest(
    FilterGroupDto? Filters = null,
    List<DatasetSortDto>? Sort = null,
    List<DatasetExportColumnDto>? Columns = null,
    string? AuthFilterColumn = null);

// Chart aggregation (Phase 2): GROUP BY category, aggregate(value). Aggregate is one of
// count | sum | avg | min | max (count ignores ValueColumn). Honors the same filter tree.
internal sealed record DatasetChartRequest(
    FilterGroupDto? Filters = null,
    string CategoryColumn = "",
    string? ValueColumn = null,
    string Aggregate = "count",
    string? AuthFilterColumn = null);
