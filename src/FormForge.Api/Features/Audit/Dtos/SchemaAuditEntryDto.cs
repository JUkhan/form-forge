namespace FormForge.Api.Features.Audit.Dtos;

// Story 5.7 — paginated schema audit log row shape. Serialized by System.Text.Json
// with default Web (camelCase) options, identical to every other endpoint. The
// "ToVersion = 0" sentinel meaning "DROP is not version-bound" is surfaced as-is
// (the integer 0); the display interpretation is the frontend's responsibility.
internal sealed record SchemaAuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid? ActorId,
    string? ActorName,
    string DesignerId,
    int? FromVersion,
    int ToVersion,
    string DdlOperation,
    string[]? ColumnsAdded,
    string[]? ColumnsDropped,
    string? ColumnsDiff,
    string? CorrelationId,
    string? Notes);
