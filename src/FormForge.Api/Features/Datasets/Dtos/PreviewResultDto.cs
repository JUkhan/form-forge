namespace FormForge.Api.Features.Datasets.Dtos;

// Story 11.3 (FR-72 AC-4) — successful preview payload. Columns holds the result-set
// column names (header row); Rows holds up to 10 data rows in column order. Cell values
// are the raw CLR types System.Text.Json serializes natively (int/long/string/bool/
// DateTime); DBNull is normalized to null before reaching here.
internal sealed record PreviewResultDto(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
