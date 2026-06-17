namespace FormForge.Api.Features.Datasets.Dtos;

// Story 8.7 (FR-62 / AR-65) — summary shape for the paginated dataset list.
// CreatedByName is null when the creator's user row has been deleted (SET NULL FK).
internal sealed record DatasetSummaryDto(
    Guid Id,
    string DatasetName,
    bool IsCustomQuery,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? CreatedByName,
    string QueryType);
