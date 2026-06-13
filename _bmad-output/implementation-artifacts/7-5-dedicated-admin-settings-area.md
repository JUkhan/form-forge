# Story 7.5: Dedicated Admin Settings Area

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Platform Admin,
I want to access a dedicated admin area containing all management pages,
so that platform configuration is separated from day-to-day data entry.

## Acceptance Criteria

**AC-1 — "Settings" link visible only to platform-admin users**
**Given** I am logged in as a Platform Admin
**When** the navbar renders
**Then** a fixed "Settings" link is visible (visible only to users with the `platform-admin` role)

**AC-2 — Admin area shows five sub-pages**
**Given** I click "Settings"
**When** the admin area opens
**Then** I see sub-pages: Users, Roles, Menus, Designers (library), Audit Logs (schema + mutation)

**AC-3 — Non-admin users are redirected**
**Given** a user without the `platform-admin` role
**When** they navigate to any `/admin/*` route
**Then** the TanStack Router `beforeLoad` guard (per Architecture minor-gap #7) throws `redirect({ to: '/' })`
**And** if they bypass the client-side guard, the server returns HTTP 403 (validating Story 2.6)

**AC-4 — Consistent admin shell layout with breadcrumbs and sub-nav**
**Given** the admin area pages
**When** I navigate between them
**Then** the admin shell layout (breadcrumbs, sub-nav) is consistent

---

## Tasks / Subtasks

- [x] **Task 1 — Rename "Admin" header link to "Settings" and add i18n keys** (AC-1)
  - [x] In `web/src/lib/i18n/locales/en.json`, add `nav.settings = "Settings"` (keep `nav.admin = "Admin"` — no removal needed; the dev agent reference in `_app.tsx` will switch to the new key)
  - [x] In `web/src/lib/i18n/locales/en.json`, add the `admin.settings` block: `breadcrumb = "Settings"`, `breadcrumbAriaLabel = "Admin settings breadcrumb"`
  - [x] In `web/src/lib/i18n/locales/en.json`, add the `admin.audit` block: `navTitle = "Audit Logs"`, `title = "Audit Logs"`, `subtitle`, `schemaSection`, `schemaDesc`, `mutationSection`, `mutationDesc`, `goToLibrary`, `goToMenus` — see Dev Notes §1 for exact values
  - [x] In `web/src/routes/_app.tsx`, change the header link from `t('nav.admin')` → `t('nav.settings')`; keep the link target as `/admin/users` (enters admin area at Users sub-page — this is correct behavior)

- [x] **Task 2 — Add Audit Logs sub-page link to admin sub-nav and add breadcrumbs** (AC-2, AC-4)
  - [x] Read `web/src/routes/_app/admin.tsx` fully before editing
  - [x] In the `AdminLayout` `<nav>` element, add a fifth `<Link to="/admin/audit">` for Audit Logs, using `t('admin.audit.navTitle')` — position it last, after the existing four links
  - [x] Add an `AdminBreadcrumb` component inside `AdminLayout` rendered above the `<nav>` — see Dev Notes §2 for the exact implementation using `useLocation()` from `@tanstack/react-router`
  - [x] Import `useLocation` from `@tanstack/react-router` at the top of `admin.tsx`
  - [x] Ensure the breadcrumb uses a semantic `<nav aria-label={t('admin.settings.breadcrumbAriaLabel')}>` with an `<ol>` list (required for screen-reader breadcrumb semantics); the first item links to `/admin/users`; the second item (current section) has `aria-current="page"`

- [x] **Task 3 — Create Audit Logs landing page** (AC-2)
  - [x] Create `web/src/routes/_app/admin/audit.tsx` — see Dev Notes §3 for the component shape
  - [x] The page must use `createFileRoute('/_app/admin/audit')` (file-based TanStack Router)
  - [x] Two sections: Schema Audit Logs and Mutation Audit Logs, each with a description paragraph and a navigation link
  - [x] Schema section links to `/designer/library` (users navigate to a specific designer and drill into its audit tab)
  - [x] Mutation section links to `/admin/menus` (users navigate to a menu binding which drills into data; this is the closest admin-area entry point for mutation audits)
  - [x] Use `t()` for every string — add i18n keys from Task 1 above; zero hardcoded English strings (AC from Story 7.6 i18n contract)
  - [x] Use `Link` from `@tanstack/react-router` for both navigation links

- [x] **Task 4 — Verify AC-3: beforeLoad guard and server-side 403** (AC-3)
  - [x] Confirm the existing `beforeLoad` in `web/src/routes/_app/admin.tsx` already checks `data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)` and throws `redirect({ to: '/' })` — this was implemented as part of Story 2.8 (review patch P11); no code change needed, but a test is required
  - [x] Confirm Story 2.6 server-side authorization is already in place for all `/api/admin/*` endpoints; no backend change needed for this story
  - [x] Document this in the completion notes

- [x] **Task 5 — Tests** (AC-1 through AC-4)
  - [x] Create `web/src/routes/_app/__tests__/admin-layout.test.tsx` — see Dev Notes §4 for the test patterns
  - [x] Test 1: `AdminLayout` renders with all five sub-nav links (Users, Roles, Menus, Component Library, Audit Logs)
  - [x] Test 2: Breadcrumb renders "Settings" as the root item and shows the current section label for `/admin/users`, `/admin/roles`, `/admin/menus`, `/admin/audit`
  - [x] Test 3: Breadcrumb root item has an accessible link; current-section item has `aria-current="page"`
  - [x] Create `web/src/routes/_app/__tests__/audit-page.test.tsx`
  - [x] Test 4: AuditLogsPage renders with both sections (schema + mutation) visible
  - [x] Test 5: Each section contains a `<Link>` to the correct destination
  - [x] Add `afterEach(cleanup)` in both new test files — this project does NOT auto-register cleanup in Vitest setup (learnt in Story 7.3, confirmed in 7.4)
  - [x] Confirm all existing 154 frontend tests still pass + new tests pass
  - [x] Confirm the 2 pre-existing `ReorderableMenuList.test.tsx` failures are unchanged

---

## Dev Notes

### §1 — i18n Keys to Add

Add these keys to `web/src/lib/i18n/locales/en.json`. Insert `nav.settings` under the existing `"nav"` block; insert `admin.settings` and `admin.audit` under the existing `"admin"` block. Do NOT remove `nav.admin` — it may be referenced in tests.

```json
"nav": {
  "settings": "Settings"
}

"admin": {
  "settings": {
    "breadcrumb": "Settings",
    "breadcrumbAriaLabel": "Admin settings breadcrumb"
  },
  "audit": {
    "navTitle": "Audit Logs",
    "title": "Audit Logs",
    "subtitle": "Track schema changes and record-level mutations across all provisioned designers.",
    "schemaSection": "Schema Audit Logs",
    "schemaDesc": "DDL operations applied to each designer's provisioned table. Open the Component Library, select a designer, then view its Audit Log tab.",
    "mutationSection": "Mutation Audit Logs",
    "mutationDesc": "Record-level create, update, soft-delete, and restore operations. Navigate to any active menu item's data entry view to access its mutation audit log.",
    "goToLibrary": "Open Component Library",
    "goToMenus": "Open Menu Configuration"
  }
}
```

**JSON merge strategy:** `en.json` is a single flat JSON object. When inserting into an existing block (e.g., `admin`), add a comma after the last existing key before the closing brace. Use the existing indentation style (2-space). Read the file first to confirm exact insertion points.

### §2 — AdminBreadcrumb Component

Add this component to `web/src/routes/_app/admin.tsx`. Import `useLocation` from `@tanstack/react-router`.

```tsx
function AdminBreadcrumb() {
  const { t } = useTranslation()
  const { pathname } = useLocation()

  // Extract first path segment after /admin/: "/admin/users/123" → "users"
  const section = pathname.replace(/^\/admin\/?/, '').split('/')[0] ?? ''

  const sectionLabels: Record<string, string> = {
    users: t('admin.users.title'),
    roles: t('admin.roles.title'),
    menus: t('admin.menus.title'),
    audit: t('admin.audit.navTitle'),
  }

  const sectionLabel = sectionLabels[section]

  return (
    <nav aria-label={t('admin.settings.breadcrumbAriaLabel')}>
      <ol style={{ display: 'flex', gap: '0.25rem', listStyle: 'none', padding: 0, margin: '0 0 0.5rem' }}>
        <li>
          <Link to="/admin/users">{t('admin.settings.breadcrumb')}</Link>
        </li>
        {sectionLabel && (
          <>
            <li aria-hidden>{'›'}</li>
            <li aria-current="page">{sectionLabel}</li>
          </>
        )}
      </ol>
    </nav>
  )
}
```

Place `<AdminBreadcrumb />` as the first child of the returned JSX in `AdminLayout`, before the `<nav aria-label="admin">` element.

**Note:** The designer library (`/designer/library`) is linked from the admin sub-nav but is NOT under `/admin/*`. Its route is outside the admin layout, so `AdminBreadcrumb` will not render for it (AdminLayout only mounts for `/admin/*` routes). This is intentional for v1 — the designer library is a shared route, not an admin-only page, and its entry from the admin nav is a convenience link.

### §3 — Audit Logs Landing Page (`admin/audit.tsx`)

```tsx
import { createFileRoute, Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'

export const Route = createFileRoute('/_app/admin/audit')({
  component: AuditLogsPage,
})

function AuditLogsPage() {
  const { t } = useTranslation()
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem' }}>
      <div>
        <h1 style={{ fontSize: '1.25rem', fontWeight: 600 }}>{t('admin.audit.title')}</h1>
        <p style={{ color: '#666', marginTop: '0.25rem' }}>{t('admin.audit.subtitle')}</p>
      </div>

      <section style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        <h2 style={{ fontSize: '1rem', fontWeight: 600 }}>{t('admin.audit.schemaSection')}</h2>
        <p style={{ color: '#555' }}>{t('admin.audit.schemaDesc')}</p>
        <Link to="/designer/library" style={{ display: 'inline-flex', alignItems: 'center', gap: '0.25rem' }}>
          {t('admin.audit.goToLibrary')}
        </Link>
      </section>

      <section style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        <h2 style={{ fontSize: '1rem', fontWeight: 600 }}>{t('admin.audit.mutationSection')}</h2>
        <p style={{ color: '#555' }}>{t('admin.audit.mutationDesc')}</p>
        <Link to="/admin/menus" style={{ display: 'inline-flex', alignItems: 'center', gap: '0.25rem' }}>
          {t('admin.audit.goToMenus')}
        </Link>
      </section>
    </div>
  )
}
```

**No `beforeLoad` needed on this route** — the parent `/_app/admin` route's `beforeLoad` already guards all `/admin/*` routes.

**Styling convention:** This codebase uses inline `style` props for component-specific layout, not Tailwind utility classes, in admin pages. Follow the same pattern as the existing `admin.tsx` layout and `users.tsx` page.

### §4 — Test Patterns

**`admin-layout.test.tsx`** — The admin layout uses TanStack Router's `Link` and `useLocation`. Mock these using the pattern from existing route tests in this codebase. If no existing route-level tests exist, use the minimal mock approach:

```tsx
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from '...' // adapt to TanStack Router test harness

afterEach(cleanup)

// NOTE: TanStack Router does not export MemoryRouter. Wrap with a real router
// instance using createMemoryHistory + RouterProvider (see how _app.test.tsx
// or login.test.tsx set up their router — use the same pattern).
// If no existing router test harness exists, create a minimal one:

function renderWithRouter(ui: React.ReactNode, initialPath = '/admin/users') {
  const router = createRouter({
    routeTree: ...,  // use the real route tree or a minimal test tree
    history: createMemoryHistory({ initialEntries: [initialPath] }),
  })
  return render(<RouterProvider router={router} />)
}
```

**If TanStack Router test harness is complex,** test `AdminBreadcrumb` in isolation by mocking `useLocation` from `@tanstack/react-router`:

```tsx
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-router')>()
  return {
    ...actual,
    useLocation: vi.fn().mockReturnValue({ pathname: '/admin/users' }),
    Link: ({ to, children }: { to: string; children: React.ReactNode }) => (
      <a href={to}>{children}</a>
    ),
  }
})
```

Then render `AdminBreadcrumb` directly and assert:
- `screen.getByText('Settings')` is a link pointing to `/admin/users`
- `screen.getByText('Users')` has `aria-current="page"`

For `audit-page.test.tsx`, render `AuditLogsPage` in isolation (no router needed since the component only renders `Link` children — mock `Link` as above):

```tsx
it('renders both audit sections with navigation links', () => {
  const { container } = render(<AuditLogsPage />)
  expect(screen.getByText('Schema Audit Logs')).toBeInTheDocument()
  expect(screen.getByText('Mutation Audit Logs')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Open Component Library' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Open Menu Configuration' })).toBeInTheDocument()
})
```

**Find the existing test harness pattern** by searching `web/src/routes/_app/` for `*.test.tsx` or `__tests__/` directories. If the harness already wraps TanStack Router with a `MemoryRouter` or `RouterProvider`, reuse it — don't invent a new one.

### §5 — File Locations

| Action | Path | Purpose |
|--------|------|---------|
| MODIFY | `web/src/lib/i18n/locales/en.json` | Add `nav.settings`, `admin.settings.*`, `admin.audit.*` keys |
| MODIFY | `web/src/routes/_app.tsx` | Change `t('nav.admin')` → `t('nav.settings')` in header link |
| MODIFY | `web/src/routes/_app/admin.tsx` | Add Audit Logs sub-nav link + `AdminBreadcrumb` component |
| CREATE | `web/src/routes/_app/admin/audit.tsx` | Audit Logs landing page (schema + mutation) |
| CREATE | `web/src/routes/_app/__tests__/admin-layout.test.tsx` | Tests for admin nav (5 links) and breadcrumb rendering |
| CREATE | `web/src/routes/_app/__tests__/audit-page.test.tsx` | Tests for AuditLogsPage sections and links |

### §6 — Critical Do-Nots

- **Do NOT remove `nav.admin`** from `en.json` — only add `nav.settings`. If there are test assertions checking `t('nav.admin')`, they will still pass.
- **Do NOT add a new `beforeLoad` to `admin/audit.tsx`** — the parent `/_app/admin` beforeLoad already covers all `/admin/*` children. Adding it again is redundant and creates a double-fetch of the permissions endpoint.
- **Do NOT move the designer library route** (`/designer/library`) into `/admin/designers/library` — that is a larger refactor outside this story's scope. The admin sub-nav simply links to the existing route.
- **Do NOT use `aria-hidden` on the `›` separator** in the breadcrumb — use the `aria-hidden` attribute directly on the `<li>` element (see §2): `<li aria-hidden>{'›'}</li>`. The string `›` is already in the JSX as a literal; no encoding needed.
- **Do NOT attempt to fix the pre-existing 2 `ReorderableMenuList.test.tsx` failures** — 154 pass / 2 fail is the current baseline.
- **Do NOT add `aria-live` or dynamic announcement regions** to the admin layout — this is static navigation chrome, not a dynamic content area.
- **Do NOT inline English strings** in the new `audit.tsx` component — every string must use `t()`. AC from Story 7.6 i18n contract is already in effect. The dev agent who works Story 7.6 will pick up any hardcoded strings as violations.

### §7 — Current Admin Layout State (READ THIS BEFORE EDITING)

The current `web/src/routes/_app/admin.tsx` renders:

```tsx
function AdminLayout() {
  const { t } = useTranslation()
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '1rem' }}>
      <nav aria-label="admin" style={{ display: 'flex', gap: '1rem', borderBottom: '1px solid #ddd', paddingBottom: '0.5rem' }}>
        <Link to="/admin/users" activeProps={{ style: { fontWeight: 600 } }}>
          {t('admin.users.title')}
        </Link>
        <Link to="/admin/roles" activeProps={{ style: { fontWeight: 600 } }}>
          {t('admin.roles.title')}
        </Link>
        <Link to="/admin/menus" activeProps={{ style: { fontWeight: 600 } }}>
          {t('admin.menus.title')}
        </Link>
        <Link to="/designer/library" activeProps={{ style: { fontWeight: 600 } }}>
          {t('designer.nav.library')}
        </Link>
      </nav>
      <Outlet />
    </div>
  )
}
```

This story adds:
1. `<AdminBreadcrumb />` as the **first element** inside the outer `<div>`
2. A fifth `<Link to="/admin/audit">` inside the `<nav aria-label="admin">` element

The outer `<div>`, the `<nav>` element and its `aria-label`, and the existing four links must be preserved exactly as-is.

### §8 — Current `_app.tsx` Admin Link State (READ THIS BEFORE EDITING)

```tsx
<PermissionGate allowed={canSeeAdmin}>
  <Link
    to="/admin/users"
    className="inline-flex min-h-[44px] min-w-[44px] items-center justify-center px-2"
  >
    {t('nav.admin')}
  </Link>
</PermissionGate>
```

Change ONLY `t('nav.admin')` → `t('nav.settings')`. The `to`, `className`, and `PermissionGate` wrapper must not change. The `canSeeAdmin` computation (`usePermission('platform-admin', 'canRead')`) is already correct — platform-admin role bypass returns `true` for the guard; non-admins get `false`.

### §9 — Architecture Compliance

| AC | Architecture Reference | Implementation |
|----|----------------------|----------------|
| AC-1 | Minor-gap #7: frontend route-level admin guard | `usePermission('platform-admin', 'canRead')` already gating the nav link in `_app.tsx`; label rename only |
| AC-2 | FR: Admin settings area covers Users, Roles, Menus, Designers, Audit Logs | Add Audit Logs link to admin sub-nav + create audit landing page |
| AC-3 | Minor-gap #7: `beforeLoad` guard on `/admin/*` | Already implemented in `admin.tsx` (Story 2.8 P11); verify + test |
| AC-4 | UX consistency: breadcrumbs + sub-nav | Add `AdminBreadcrumb` component to admin layout |

### §10 — Previous Story Learnings (from 7.4)

- `afterEach(cleanup)` is REQUIRED in every new `*.test.tsx` file — this project does NOT auto-register Vitest cleanup. Missing it causes test state to leak.
- The 2 pre-existing `ReorderableMenuList.test.tsx` failures are unrelated noise; the current baseline is **154 pass / 2 fail**. New tests should bring it to 157+ pass / 2 fail (or whatever count the new tests add).
- When mocking `@tanstack/react-router` hooks (`useLocation`, `Link`), use `vi.mock` with `async (importOriginal)` to spread the real module and override only the needed exports — avoids breaking other router utilities that the component under test may transitively import.
- Inline `style` props (not Tailwind classes) are the dominant pattern in admin route components. Follow what's already in `admin.tsx` and `users.tsx` for consistency.
- Import `useTranslation` from `react-i18next` (not from a re-export) — that's the pattern in all admin components.

### §11 — Git Intelligence

- `fd4b4f9` (Story 7.4) — last commit. It touched `playwright.config.ts` (added webServer block), `web/package.json` (added axe-core deps), `.github/workflows/ci.yml`. No changes to admin routes, `_app.tsx`, or `en.json` in that commit.
- The current admin nav in `admin.tsx` has 4 links. The designer library link (`/designer/library`) goes outside `/admin/*` — this is intentional and was implemented in one of the earlier admin stories (2.8 or 3.8).
- The `nav.admin = "Admin"` key was added in an early story (likely 2.8 when the admin link was first introduced). Adding `nav.settings` as a new key alongside it is safe.
- The `PLATFORM_ADMIN_ROLE_ID` constant (`00000000-0000-0000-0000-000000000001`) is seeded in the database. Do not hardcode the UUID elsewhere — import from `usePermissionsQuery.ts`.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (1M context)

### Debug Log References

- `npx vitest run src/routes/_app/__tests__/` → 2 files, 10 tests pass
- `npx vitest run` → 164 pass / 2 fail (10 new + 154 prior; the 2 failures remain the pre-existing `ReorderableMenuList.test.tsx` ones called out in Dev Notes §10)
- `npx tsc -b --noEmit` → no new TS errors introduced (pre-existing baseline in `data.$designerId.tsx`, `DataEntryPage.tsx`, `RecordDetailPage.tsx`, `RecordListPage.tsx`, `usePollProvisioning.test.tsx`, `Navbar.test.tsx` unchanged)
- `npx eslint src/routes/_app/admin.tsx src/routes/_app/admin/audit.tsx src/routes/_app/__tests__/` → only the codebase-wide `react-refresh/only-export-components` baseline (34 such errors exist across all route files); new test files lint-clean

### Completion Notes List

- **AC-1 (Settings link visible only to platform-admin)**: header link in `web/src/routes/_app.tsx:99` switched from `t('nav.admin')` → `t('nav.settings')`. The link is still wrapped by `<PermissionGate allowed={canSeeAdmin}>` where `canSeeAdmin = usePermission('platform-admin', 'canRead')` — non-admins continue to receive `false` and the link is absent (not disabled). `nav.admin` key retained per Dev Notes §6.
- **AC-2 (five sub-pages)**: `AdminLayout` in `web/src/routes/_app/admin.tsx` now renders five `<Link>` children — Users, Roles, Menus, Component Library (`/designer/library`), and Audit Logs (`/admin/audit`). The new Audit Logs landing page at `web/src/routes/_app/admin/audit.tsx` provides Schema and Mutation sections with navigation links to `/designer/library` (designer audit drill-in) and `/admin/menus` (mutation audit drill-in).
- **AC-3 (non-admin redirect + server 403)**: Verified the existing `beforeLoad` in `admin.tsx:11–29` already calls `ensureQueryData` on `/api/users/me/permissions`, checks `roleIds.includes(PLATFORM_ADMIN_ROLE_ID)`, and throws `redirect({ to: '/' })` on miss — implemented under Story 2.8 review patch P11. Server-side authorization on `/api/admin/*` returning HTTP 403 to non-admins is already covered by Story 2.6. No code change required for AC-3.
- **AC-4 (consistent admin shell with breadcrumbs)**: `AdminBreadcrumb` added as a sibling component in `admin.tsx`; rendered as the first child of the layout above the existing sub-nav. Uses `useLocation` from `@tanstack/react-router` to extract the first path segment after `/admin/`, semantic `<nav><ol>` markup with `aria-label`, root item links to `/admin/users`, and the current section receives `aria-current="page"`. The `›` separator `<li>` carries `aria-hidden` so screen readers skip it.
- **Code-split warnings (informational)**: TanStack Router emits "exports … will not be code-split" hints for `AdminLayout` (exported for testing) and `AuditLogsPage` (exported for testing). The component definitions per Dev Notes §2/§3 are required to live inside the route files, so the hints are accepted as a minor bundle-size trade-off; the codebase already mixes `Route` with route-component exports in every other admin route (34 baseline `react-refresh/only-export-components` errors).
- **Test harness**: Used the §4 mock pattern — `vi.mock('@tanstack/react-router', ...)` to stub `useLocation`, render `<Link>` as a plain `<a>`, render `<Outlet>` as `null`, and short-circuit `createFileRoute`. Avoids the need for a real `RouterProvider`/`createMemoryHistory` harness (none exists in this codebase yet). `afterEach(cleanup)` is registered in both new files per Dev Notes §10.
- **vite.config.ts**: added `routeFileIgnorePattern: '__tests__|\\.test\\.'` to the `tanstackRouter` plugin so test files under `routes/**/__tests__/` are no longer scanned as route files (silences the "does not export a Route" warnings without renaming the test files).
- **Per-AC tests**: `admin-layout.test.tsx` (1 AdminLayout test + 4 breadcrumb path tests + 1 aria-label test + 1 aria-current/link test + 1 no-section-omission test) and `audit-page.test.tsx` (1 sections-render test + 1 link-destinations test) — covering AC-1 indirectly (rename), AC-2 (5 nav links + audit page sections), AC-4 (breadcrumb root + current-section semantics).

### File List

**Created:**
- `web/src/routes/_app/admin/audit.tsx`
- `web/src/routes/_app/__tests__/admin-layout.test.tsx`
- `web/src/routes/_app/__tests__/audit-page.test.tsx`

**Modified:**
- `web/src/lib/i18n/locales/en.json`
- `web/src/routes/_app.tsx`
- `web/src/routes/_app/admin.tsx`
- `web/vite.config.ts`

### Review Findings

- [x] [Review][Decision] `sectionLabels` missing `designers` and `data` keys for existing admin sub-routes — Routes `/admin/designers/$designerId/*` (Stories 5.6, 5.7) and `/admin/data/$designerId/*` (Story 6.8) are under `/_app/admin` and are guarded by the admin `beforeLoad`, yet their first path segments (`"designers"`, `"data"`) are absent from `sectionLabels`. Navigating to any of these routes renders `AdminBreadcrumb` with only the root "Settings" link and no current-section label. Decision: add the missing keys now (low effort) or defer to a follow-up story with an explanatory comment. [admin.tsx:sectionLabels] → **patched**: added `admin.designers.navTitle = "Designers"` and `admin.data.navTitle = "Data"` to en.json; added `designers` and `data` entries to `sectionLabels`; 2 new parameterized test cases added to admin-layout.test.tsx.

- [x] [Review][Patch] `routeFileIgnorePattern` does not cover `.spec.` files — extended to `'__tests__|\\.test\\.|\\.spec\\.'`. [web/vite.config.ts]

- [x] [Review][Patch] Tests assert `.not.toBeNull()` on `getByText` — replaced all redundant `.not.toBeNull()` chains on `getByText` with bare calls in admin-layout.test.tsx and audit-page.test.tsx. querySelector-based assertions retained. [web/src/routes/_app/__tests__/admin-layout.test.tsx, audit-page.test.tsx]

- [x] [Review][Patch] No test for AC-3 `beforeLoad` redirect guard — added `describe('Admin route beforeLoad guard (AC-3)')` with 2 tests using `vi.hoisted` + `routeCapture` to extract the beforeLoad from the mocked `createFileRoute`; asserts `redirect({ to: '/' })` is thrown for non-admin, resolves for admin. [web/src/routes/_app/__tests__/admin-layout.test.tsx]

- [x] [Review][Patch] No test asserts `<li aria-hidden>` separator in `AdminBreadcrumb` — added test `'separator <li aria-hidden> is present between root and current-section items'` querying `ol li[aria-hidden]` and asserting text content `'›'`. [web/src/routes/_app/__tests__/admin-layout.test.tsx]

- [x] [Review][Defer] Breadcrumb root always links to `/admin/users` instead of a true admin root — Spec §2 prescribes `<Link to="/admin/users">` exactly; there is no `/admin` index route. On the Users sub-page the breadcrumb reads "Settings › Users" where the root link and the current page resolve to the same URL. Pre-existing spec design decision; would require an admin index route to resolve cleanly. [admin.tsx:52] — deferred, pre-existing

- [x] [Review][Defer] Hardcoded hex colors (`#666`, `#555`) in `AuditLogsPage` bypass CSS-variable theme system — Pre-existing inline-style pattern across all admin pages (admin.tsx uses `#ddd`); spec §3 prescribed these values. Not a regression of this story. [web/src/routes/_app/admin/audit.tsx] — deferred, pre-existing

- [x] [Review][Defer] `AdminBreadcrumb` and `AuditLogsPage` exported from route files (TanStack Router bundle-split warnings) — Exports are required to enable direct test imports under the chosen mock-based test harness; no real router harness exists in this codebase. Accepted trade-off acknowledged in completion notes. [admin.tsx, admin/audit.tsx] — deferred, pre-existing

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-27 | Story 7.5 created — Dedicated Admin Settings Area | bmad-create-story |
| 2026-05-27 | Story 7.5 implemented — Settings link rename, AdminBreadcrumb, Audit Logs sub-page (5 nav links), i18n keys, 10 new tests; tanstackRouter `routeFileIgnorePattern` set so `__tests__` is excluded from the route tree | bmad-dev-story |
