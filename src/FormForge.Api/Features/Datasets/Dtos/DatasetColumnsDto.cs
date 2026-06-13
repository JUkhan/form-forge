namespace FormForge.Api.Features.Datasets.Dtos;

// Designer Dropdown "Dataset" source — the column names exposed by a dataset's
// backing VIEW (datasets."<name>"). Drives the inspector's Label/Value field
// comboboxes so a form author picks real columns. Returned by
// GET /api/datasets/{id}/columns (auth-only — the form author needs it while the
// runtime filler later reads options through GET /api/datasets/{id}/options).
internal sealed record DatasetColumnsDto(
    Guid Id,
    string DatasetName,
    IReadOnlyList<string> Columns);
