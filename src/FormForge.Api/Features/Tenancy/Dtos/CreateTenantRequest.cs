namespace FormForge.Api.Features.Tenancy.Dtos;

// Decision (human-approved) — takes only name + schema_name, matching epics.md's AC
// literally. The first tenant-admin's email/displayName/password are all derived or
// generated server-side by TenantEndpoints.CreateTenantHandler; never accepted here.
internal sealed record CreateTenantRequest(string Name, string SchemaName);

// The generated temporary password is returned exactly once, in the create response —
// it is never persisted in plaintext beyond its BCrypt hash (written by
// ITenantOnboardingService via IPasswordHasher). The platform-super-admin relays it to
// the tenant out-of-band.
internal sealed record CreateTenantResponse(TenantDto Tenant, string TemporaryPassword);
