namespace FormForge.Api.Features.Designer.Dtos;

// Story 5.6 — drift view payload. Each orphaned column carries its PG data type
// (so the operator can judge what kind of data they would lose) and an estimate
// of how many rows have a non-null value in the column. The COUNT(*) is exact
// at query time but labelled "Estimated" because it may be stale by the time
// the operator clicks "Drop Column" — that's an acceptable race for an admin
// inspection tool.
internal sealed record OrphanedColumnInfo(
    string ColumnName,
    string PgDataType,
    long EstimatedNonNullRowCount);

internal sealed record SchemaDriftResponse(IReadOnlyList<OrphanedColumnInfo> OrphanedColumns);
