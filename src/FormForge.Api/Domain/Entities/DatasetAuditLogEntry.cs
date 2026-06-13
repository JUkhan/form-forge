namespace FormForge.Api.Domain.Entities;

// Story 8.1 (FR-55, AR-57) — append-only audit log for Dataset CREATE/UPDATE/DELETE
// operations. Operation is constrained by a DB CHECK (CREATE|UPDATE|DELETE). There is
// no FK navigation property to users on ActorId by design: the log is append-only and
// ActorId may be null for system operations. A DB-level FK to users(id) still exists
// (configured without a navigation in FormForgeDbContext). Column mapping is fluent.
internal sealed class DatasetAuditLogEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string DatasetName { get; set; } = string.Empty;

    // CHECK (operation IN ('CREATE', 'UPDATE', 'DELETE')) — enforced at the DB level.
    public string Operation { get; set; } = string.Empty;

    public string? PreviousValues { get; set; }   // jsonb
    public string? NewValues { get; set; }         // jsonb
    public string? Ddl { get; set; }
    public bool Succeeded { get; set; } = true;
    public string? CorrelationId { get; set; }
}
