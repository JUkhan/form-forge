namespace FormForge.Api.Features.Audit.Dtos;

// Story 6.8 — paginated mutation audit log row shape. Mirrors SchemaAuditEntryDto
// but for CRUD operations. previousValues/newValues are JSONB string snapshots —
// returned as raw strings (the display interpretation is the frontend's responsibility).
internal sealed record MutationAuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid? ActorId,
    string? ActorName,
    string DesignerId,
    Guid RecordId,
    string Operation,
    string? PreviousValues,
    string? NewValues,
    string? CorrelationId);
