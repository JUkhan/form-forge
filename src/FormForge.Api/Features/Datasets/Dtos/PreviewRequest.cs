namespace FormForge.Api.Features.Datasets.Dtos;

// Story 11.3 (FR-72 / AR-63) — request body for POST /api/datasets/preview.
// IsCustomQuery selects which payload field the server runs: Query (raw SELECT,
// re-validated SELECT-only) in custom mode, or BuilderState (re-derived to SQL via
// DatasetSqlGenerator) in builder mode. Both are bounded to LIMIT 10 server-side.
internal sealed record PreviewRequest(
    bool IsCustomQuery,
    string? Query,
    string? BuilderState);
