// Mirrors the backend DTOs at src/FormForge.Api/Features/Tenancy/Dtos/.
// Keep in sync when the wire shape changes.

export type TenantStatus = 'Provisioning' | 'Active' | 'Suspended' | 'Error'

export interface TenantListItem {
  id: string
  name: string
  schemaName: string
  status: TenantStatus
  createdAt: string
}

// Decision (human-approved) — the Create Tenant form takes only name + schema_name;
// the first tenant-admin's email/displayName/password are all server-derived.
export interface CreateTenantRequest {
  name: string
  schemaName: string
}

// The generated temporary password is returned exactly once, here — never persisted
// in plaintext beyond its hash. The platform-super-admin relays it to the tenant
// out-of-band.
export interface CreateTenantResponse {
  tenant: TenantListItem
  temporaryPassword: string
  // Hidden per-tenant platform-dev login. Present only in the create response; the
  // tenant list never exposes either field (the password is stored encrypted server-side).
  devUserEmail?: string
  devUserPassword?: string
}
