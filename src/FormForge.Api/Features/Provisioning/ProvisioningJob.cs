namespace FormForge.Api.Features.Provisioning;

// Story 5.2 — work item dropped on the Channel by MenuService and consumed by
// ProvisioningBackgroundService. DesignerId is already validated against the
// SafeIdentifier regex by BindMenuDesignerRequestValidator before reaching here;
// Story 5.3's DdlEmitter MUST still wrap it in SafeIdentifier.TryCreate() before
// any SQL interpolation per Decision 1.1 (defence in depth).
//
// Story 5.4 added FromVersion: the previous BoundVersion captured by
// MenuService.BindDesignerAsync before overwriting it. null means either a
// first-time CREATE or a Retry (which does not know the historical previous
// version without an audit-log lookup, deferred to a later story).
//
// MenuId is nullable: a null MenuId is a "menu-less" provisioning job enqueued
// from the admin Table Provisioning tab — the table is created/synced directly
// from a Designer with no menu binding. ProvisioningBackgroundService skips the
// menu status write for these and relies on table-existence + the schema audit
// log to surface status (DdlEmitter never reads MenuId, so the emit is identical).
// Story 12.6 follow-up (architecture.md Decision 7.7) — TenantId/TenantSchema carry the
// enqueueing request's tenant across the Channel. They have to live ON THE JOB rather than
// be resolved by the consumer: ProvisioningBackgroundService is a Singleton draining a
// queue long after the originating request's DI scope was disposed, so there is no ambient
// ITenantContext for it to read. ProvisioningService stamps them at enqueue time; the
// consumer replays them onto the scope it creates per job.
//
// Both null means "no tenant" — a legacy single-tenant deployment, or a job recovered from
// the `public` schema — which keeps the connection on `public`, exactly as before.
internal sealed record ProvisioningJob(
    Guid? MenuId,
    string DesignerId,
    int Version,
    Guid? ActorId,
    int? FromVersion = null,
    Guid? TenantId = null,
    string? TenantSchema = null);
