namespace FormForge.Api.Features.Audit.Dtos;

// Story 8.9 (FR-61 / AR-65) — paginated dataset audit log row shape. Consumed by
// GET /api/admin/datasets/audit. PreviousValues and NewValues are raw JSONB strings
// (the client may parse them for diff display). ActorName is stored directly in the
// audit row — unlike schema/mutation audit logs, no join to users is needed.
// Succeeded = false marks a rolled-back DDL attempt (FR-61 AC-2 / AR-57).
internal sealed record DatasetAuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid? ActorId,
    string? ActorName,
    string DatasetName,
    string Operation,          // "CREATE" | "UPDATE" | "DELETE"
    string? PreviousValues,    // JSONB snapshot of the row before the operation
    string? NewValues,         // JSONB snapshot of the row after the operation
    string? Ddl,               // exact SQL executed or attempted
    bool Succeeded,            // false when the transaction was rolled back (FR-61 AC-2)
    string? CorrelationId);
