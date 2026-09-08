import { createFileRoute, redirect } from '@tanstack/react-router'
import { tokenStore } from '../features/auth/tokenStore'
import { getJwtRoles } from '../features/auth/jwt'
import { TenantsPage } from '../features/admin/tenants/TenantsPage'

// Story 12.5 — standalone top-level route, sibling to _app.tsx/login.tsx (NOT nested
// inside _app/admin — Design Notes: that pathless-layout subtree already owns
// /admin/roles, /admin/users for tenant-admins, and a platform-super-admin session
// cannot resolve _app's own beforeLoad at all: no tenantId claim, and Story 12.4's
// access-token-only decision means no refresh token ever exists for this tier to
// silently refresh with).
//
// So this guard is deliberately narrower than _app.tsx's: no refreshSession() slow
// path (there is nothing to refresh), no useAuthQuery/usePermissionsQuery (both
// assume a tenant session) — just tokenStore + the JWT "roles" claim, read directly.
// A session that expires AFTER this initial check is caught by this page's own first
// API call 401'ing through httpClient's existing hard-redirect-to-/login path
// (httpClient.ts) — that is the sole session-expiry path for this route.
export const Route = createFileRoute('/admin/tenants')({
  beforeLoad: () => {
    const token = tokenStore.get()
    if (!token || !getJwtRoles(token).includes('platform-super-admin')) {
      throw redirect({ to: '/login' })
    }
  },
  component: TenantsPage,
})
