namespace FormForge.Api.Features.Datasets.Dtos;

// Story 8.3 — minimal shape needed for dataset_name validation. Stories 8.4/8.8
// flesh out the create handler (query / builder_state semantics).
// Story 11.2 (FR-71 AC-4) — BuilderState lets a builder-mode create persist the raw
// canvas state and re-derive its SQL on the server (CreateAsync checkpoint (b)).
internal sealed record CreateDatasetRequest(
    string DatasetName,
    bool IsCustomQuery,
    string? Query,
    string? BuilderState);
