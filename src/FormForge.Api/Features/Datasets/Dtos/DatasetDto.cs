namespace FormForge.Api.Features.Datasets.Dtos;

// Story 8.4 (FR-58) — Dataset response shape returned by the create handler (201
// body) and reused by Stories 8.5 (update) and 8.7 (get). Mirrors the
// custom_dataset row columns the API exposes.
internal sealed record DatasetDto(
    Guid Id,
    string DatasetName,
    bool IsCustomQuery,
    string? Query,
    string? BuilderState,
    int Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    string QueryType);
