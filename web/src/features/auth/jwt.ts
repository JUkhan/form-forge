// Story 12.5 — minimal, dependency-free JWT payload decode used ONLY to read the
// "roles" claim for the standalone /admin/tenants route's beforeLoad guard (Boundaries:
// that route cannot use useAuthQuery/usePermissionsQuery — both assume a tenant
// session). Deliberately not a full JWT library: no signature verification (the token
// was already validated server-side; this is a client-side navigation gate, not a
// security boundary — the API itself enforces RequirePlatformSuperAdmin()) and no
// expiry check (an expired token still fails the page's own first API call via
// httpClient's existing 401 -> hard-redirect-to-/login path).
export function getJwtRoles(token: string): string[] {
  try {
    const payload = token.split('.')[1]
    if (!payload) return []

    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/')
    const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4)
    const decoded = JSON.parse(atob(padded)) as { roles?: string | string[] }

    if (!decoded.roles) return []
    return Array.isArray(decoded.roles) ? decoded.roles : [decoded.roles]
  } catch {
    return []
  }
}
