namespace FormForge.Api.Features.Datasets.Dtos;

// Story 8.3 — dataset_name and query are nullable: omitting them means "keep the
// existing value". version is always required for optimistic concurrency (Story 8.5).
internal sealed record UpdateDatasetRequest(
    string? DatasetName,
    bool? IsCustomQuery,
    string? Query,
    string? BuilderState,
    int Version);
