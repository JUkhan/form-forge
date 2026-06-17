namespace FormForge.Api.Features.Datasets.Dtos;

// Story 11.3 (FR-72 / AR-63) — request body for POST /api/datasets/preview.
// IsCustomQuery selects which payload field the server runs: Query (raw SELECT,
// re-validated SELECT-only) in custom mode, or BuilderState (re-derived to SQL via
// DatasetSqlGenerator) in builder mode. Both are bounded to LIMIT 10 server-side.
//
// Parameterized-query feature — for QueryType "query" the raw Query may contain
// {_placeholder} tokens. QueryParameters is a JSON object string mapping each placeholder
// name (e.g. "_age") to its resolved value; the server binds them as positional parameters
// before executing. Condition, when non-empty, is appended as an additional WHERE clause on
// the wrapped query. Both are ignored for "view" datasets and builder mode.
internal sealed record PreviewRequest(
    bool IsCustomQuery,
    string? Query,
    string? BuilderState,
    string? QueryType = null,
    string? QueryParameters = null,
    string? Condition = null);
