namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 — describes one user-defined column produced by a Designer field
// element. The four fields collectively let Epic 6 CRUD code:
//   ColumnName  → build SELECT/INSERT statements (validated fieldKey)
//   PgType      → bind parameters with the correct NpgsqlDbType
//   ComponentType → preserve the originating component for debugging / metadata
//   IsImage     → drives MinIO presigned URL serialization on read
internal sealed record ColumnDefinition(
    string ColumnName,      // validated fieldKey — becomes the PG column name
    string PgType,          // TEXT | NUMERIC | BOOLEAN | TIMESTAMPTZ | UUID | JSONB
    string ComponentType,   // TextInput | NumberInput | Image | ...
    bool IsImage);
