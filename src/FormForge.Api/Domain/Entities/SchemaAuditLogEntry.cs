namespace FormForge.Api.Domain.Entities;

// Story 5.3 — append-only audit row written after a successful DDL emit. Captures
// the (designerId, fromVersion, toVersion, ddlOperation, columnsAdded, correlationId,
// actorId) shape per Decision 1.5. Read by Story 5.7 (audit log view).
// Story 5.4 added ColumnsDiff: JSON snapshot of {existingUserColumns, addedColumns,
// orphanedColumns} for ALTER rows; always null for CREATE.
internal sealed class SchemaAuditLogEntry
{
    public Guid Id { get; set; }
    public string DesignerId { get; set; } = string.Empty;   // SafeIdentifier value
    public int? FromVersion { get; set; }                      // null for CREATE
    public int ToVersion { get; set; }
    public string DdlOperation { get; set; } = string.Empty;  // "CREATE" | "ALTER" | "DROP"
    public string[]? ColumnsAdded { get; set; }               // column names added by this op
    public string[]? ColumnsDropped { get; set; }             // column names removed by a DROP op (Story 5.6)
    public string? ColumnsDiff { get; set; }                   // JSON; null for CREATE (Story 5.4)
    public string? CorrelationId { get; set; }                 // ULID generated per job
    public Guid? ActorId { get; set; }                         // user who triggered the bind
    public DateTimeOffset CreatedAt { get; set; }
    // Story 5.7 — free-text annotation; always null for system-generated rows in v1.
    public string? Notes { get; set; }
}
