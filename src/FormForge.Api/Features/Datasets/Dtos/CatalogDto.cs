namespace FormForge.Api.Features.Datasets.Dtos;

// Story 9.1 (FR-63 / AR-62): Query Builder catalog response shapes.
// C# naming: PascalCase properties → camelCase JSON via default serializer options.
internal sealed record CatalogColumnDto(string ColumnName, string PgType, bool IsNullable);

// GET /api/datasets/catalog — lightweight LIST of allowlisted tables: names + column
// counts only (no columns), so the palette stays small even with thousands of tables.
// Columns are fetched per-table on demand (see TableColumnsDto) when a table is used.
internal sealed record CatalogTableDto(string TableName, int ColumnCount);
internal sealed record CatalogDto(IReadOnlyList<CatalogTableDto> Tables);

// GET /api/datasets/catalog/{table} — one allowlisted table's columns, fetched lazily
// when the table is dragged onto the canvas.
internal sealed record TableColumnsDto(string TableName, IReadOnlyList<CatalogColumnDto> Columns);
