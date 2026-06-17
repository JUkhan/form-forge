namespace FormForge.Api.Features.Datasets.Dtos;

// Story 8.3 — minimal shape needed for dataset_name validation. Stories 8.4/8.8
// flesh out the create handler (query / builder_state semantics).
// Story 11.2 (FR-71 AC-4) — BuilderState lets a builder-mode create persist the raw
// canvas state and re-derive its SQL on the server (CreateAsync checkpoint (b)).
// QueryType ("view" | "query") selects whether the dataset is materialized as a backing
// VIEW or stored as a record only (parameterized query). Null/omitted defaults to "view"
// in the service, preserving the original create contract.
internal sealed record CreateDatasetRequest(
    string DatasetName,
    bool IsCustomQuery,
    string? Query,
    string? BuilderState,
    string? QueryType = null);
