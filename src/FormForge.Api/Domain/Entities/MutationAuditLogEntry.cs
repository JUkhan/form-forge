namespace FormForge.Api.Domain.Entities;

// Story 6.3 — append-only audit row written after a successful dynamic-CRUD
// mutation. Captures (designerId, recordId, operation, actorId, timestamp,
// newValues, previousValues, correlationId) per Decision 1.5. Read by Story 6.8
// (mutation audit log view). NewValues / PreviousValues are JSONB snapshots of
// the user fieldKey columns; system columns are excluded from the JSON to keep
// the audit row compact.
internal sealed class MutationAuditLogEntry
{
    public Guid Id { get; set; }
    public string DesignerId { get; set; } = string.Empty;   // SafeIdentifier value
    public Guid RecordId { get; set; }
    public string Operation { get; set; } = string.Empty;     // "CREATE" | "UPDATE" | "SOFT_DELETE" | "RESTORE"
    public Guid? ActorId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? NewValues { get; set; }                    // JSONB — serialized payload dict
    public string? PreviousValues { get; set; }               // JSONB — null for CREATE
    public string? CorrelationId { get; set; }                // ULID, 26 chars
}
