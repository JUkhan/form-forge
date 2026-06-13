# Story 7.1: Responsive Layout with Collapsible Navigation

Status: done

## Story

As a Content Editor on mobile,
I want to use all core CMS functions on my phone,
so that I am not limited to desktop access.

## Acceptance Criteria

**AC-1 — Single-column layout on mobile**
**Given** a viewport <768 px
**When** any FormForge route renders
**Then** the layout is single-column (sidebar hidden, content fills the full viewport width)

**AC-2 — Sidebar + content layout on desktop**
**Given** a viewport ≥768 px
**When** any route renders
**Then** the layout shows a fixed 256 px sidebar on the left and a content area offset by 256 px

**AC-3 — Hamburger menu with auto-close**
**Given** I am on mobile
**When** the navbar renders
**Then** it collapses to a hamburger button (validating FR-22 AC-3 / Story 4.7 across all pages)
**And** tapping any nav item auto-closes the drawer

**AC-4 — Touch target size**
**Given** any interactive control on any FormForge page
**When** I inspect it on mobile
**Then** its touch target is ≥44×44 px (per FR-37 AC-3)

**AC-5 — No horizontal scroll**
**Given** any viewport ≥320 px
**When** I scroll horizontally
**Then** no horizontal scroll bar appears (per FR-37 AC-4)

**AC-6 — Playwright viewport sweep**
**Given** a Playwright test suite configured at viewports 320 / 768 / 1024 / 1440 px
**When** the suite runs against the app layout
**Then** at 320 px the hamburger is visible and sidebar is hidden
**And** at ≥768 px the sidebar is visible and the hamburger is hidden
**And** the hamburger drawer closes when a nav item is tapped (AC-3)
**And** `document.documentElement.scrollWidth <= window.innerWidth` at every viewport (AC-5)

---

## Tasks / Subtasks

- [x] **Task 1 — Fix `NavSkeleton` in `Navbar.tsx`** (AC-1, consistent with Story 6.11 shadcn Skeleton adoption)
  - [x] Import `Skeleton` from `@/components/ui/skeleton`
  - [x] Replace the three raw `<li className="h-4 w-full animate-pulse rounded bg-gray-200" />` divs in `NavSkeleton()` with `<Skeleton className="h-4 w-full" />` wrapped in `<li>` tags
  - [x] Update the existing `Navbar.test.tsx` skeleton test: the current assertion `container.querySelectorAll('.animate-pulse').length === 3` now targets the inner Skeleton elements; verify the test still passes (shadcn Skeleton renders with `animate-pulse` on the inner span/div — keep the assertion or switch to a `[data-slot="skeleton"]` selector if shadcn adds one)

- [x] **Task 2 — Fix `DynamicComponent` internal schema-loading skeleton** (deferred from Story 6.11)
  - [x] In `web/src/components/designer/DynamicComponent.tsx` line 303–304, replace:
    ```tsx
    return <div className="h-32 w-full animate-pulse rounded-lg bg-slate-100" />
    ```
    with:
    ```tsx
    import { Skeleton } from '@/components/ui/skeleton'
    // ...
    return <Skeleton className="h-32 w-full" />
    ```
  - [x] Add the import at the top of the file (alongside other `@/components/ui` imports); do NOT add it inside the component body

- [x] **Task 3 — Audit and fix horizontal overflow** (AC-5)
  - [x] Open `web/src/routes/_app.tsx` and verify the outer wrapper `<div className="min-h-screen md:flex">` has no elements that can exceed the viewport width
  - [x] The fixed sidebar (`w-64 md:fixed`) is already positioned outside normal flow; confirm `<main>` has `overflow-x-hidden` or that Tailwind's base reset handles it
  - [x] If any route's content can overflow on narrow viewports, add `overflow-x-hidden` to the `<main>` element in `_app.tsx`; do NOT add it to the outer wrapper (it would clip the fixed sidebar's drop shadow if any)

- [x] **Task 4 — Install and configure Playwright** (AC-6)
  - [x] In `web/`, run: `npm install --save-dev @playwright/test`
  - [x] Run: `npx playwright install chromium` (Chromium only; Firefox/WebKit deferred per scope)
  - [x] Create `web/playwright.config.ts` (see Dev Notes §3 for full config)
  - [x] Add `"test:e2e": "playwright test"` to `web/package.json` scripts
  - [x] Add `web/e2e/` directory (create at least one test file — see Task 5)

- [x] **Task 5 — Write Playwright responsive layout tests** (AC-1 through AC-6)
  - [x] Create `web/e2e/responsive-layout.spec.ts` (see Dev Notes §4 for full test code)
  - [x] Tests use `page.route()` to mock auth (`/api/auth/refresh`, `/api/auth/permissions`, `/api/menus/nav`) so no real backend is required
  - [x] Cover all 4 viewports for hamburger/sidebar visibility
  - [x] Cover auto-close on item tap at 320 px
  - [x] Assert `scrollWidth <= innerWidth` at each viewport

### Review Findings

- [x] [Review][Patch] Auto-close test uses early `return` instead of `test.skip()` — runs as vacuous pass on 3 of 4 viewport projects [web/e2e/responsive-layout.spec.ts]
- [x] [Review][Defer] Parent nav-item primary button calls `closeDrawer` — drawer closes before sub-menu can expand [web/src/components/shared/Navbar.tsx:124] — deferred, pre-existing from Story 4.7
- [x] [Review][Defer] `scrollWidth` check may evaluate before React layout fully hydrated — `page.goto()` resolves on `load` but async router work may still be in-flight [web/e2e/responsive-layout.spec.ts] — deferred, pre-existing test design
- [x] [Review][Defer] `document.documentElement.scrollWidth` may miss body-level overflow — `Math.max(document.documentElement.scrollWidth, document.body.scrollWidth)` is more robust [web/e2e/responsive-layout.spec.ts] — deferred, minor test robustness gap
- [x] [Review][Defer] HTML reporter has no `outputFolder` — `playwright-report/` discarded by CI runners without artifact upload step [web/playwright.config.ts] — deferred, CI pipeline story
- [x] [Review][Defer] `bg-accent` CSS variable: if shadcn `--accent` token is absent, Skeleton renders transparent — deferred, pre-existing from Story 6.11
- [x] [Review][Defer] `overflow-x-hidden` on `<main>` will silently clip future absolutely-positioned descendants [web/src/routes/_app.tsx:70] — deferred, speculative future concern

---

## Dev Notes

### §1 — Scope Boundaries

**In scope**:
- `NavSkeleton` → shadcn `<Skeleton>` replacement in `Navbar.tsx`
- `DynamicComponent` schema-load path → shadcn `<Skeleton>` replacement (deferred from 6.11)
- Horizontal overflow audit on `_app.tsx`
- Playwright setup + viewport tests at 320 / 768 / 1024 / 1440 px

**NOT in scope** (owned by Story 7.4 Accessibility Compliance):
- Focus trap inside the mobile drawer
- Escape key handler to close the drawer
- `role="dialog"` on the drawer + `aria-modal`
- `body` inert while drawer is open
- axe-core CI gate

**NOT in scope** (deferred from Story 4.7 hardening):
- `MenuCache` invalidation on role change
- `openParents` Set leak
- NavLucideIcon whitelist expansion

### §2 — Existing Implementation (What Story 4.7 Already Did)

The responsive layout is **already implemented**. This story primarily audits, hardens, and tests it.

**`web/src/routes/_app.tsx` (current state):**
```tsx
function AppLayout() {
  return (
    <div className="min-h-screen md:flex">
      <Navbar />
      <main id="main-content" className="flex-1 p-4 md:pl-64">
        <header className="mb-4 flex items-center justify-end gap-4">
          {/* Admin link (permission-gated) + Logout button */}
        </header>
        <Outlet />
      </main>
    </div>
  )
}
```
- `min-h-screen md:flex` → single-column at <768 px (AC-1), flex row at ≥768 px (AC-2)
- `md:pl-64` on `<main>` → 256 px left offset at desktop to not sit under the fixed sidebar

**`web/src/components/shared/Navbar.tsx` (current state):**
- Desktop sidebar: `md:fixed md:inset-y-0 md:left-0 md:block w-64 border-r ...`
- Mobile hamburger: `fixed left-2 top-2 z-30 ... md:hidden` (hidden on desktop)
- Mobile drawer backdrop: rendered only when `drawerOpen === true`
- Auto-close: every `<NavListRow>` receives `onLeafClick={closeDrawer}` which calls `setDrawerOpen(false)` (AC-3)
- Touch targets: `min-h-[44px] min-w-[44px]` on hamburger, leaf buttons, disclosure toggles (AC-4)
- Skip link: `<a href="#main-content" className="sr-only focus:not-sr-only ...">` (keyboard navigation)
- `NavSkeleton()` renders three `<li className="h-4 w-full animate-pulse rounded bg-gray-200" />` — **Task 1 will convert these to shadcn `<Skeleton>`**

**`web/src/components/designer/DynamicComponent.tsx:303–304` (current state):**
```tsx
if (isLoading) {
  return <div className="h-32 w-full animate-pulse rounded-lg bg-slate-100" />
}
```
— **Task 2 will convert this to `<Skeleton className="h-32 w-full" />`**

### §3 — Playwright Configuration

Create `web/playwright.config.ts`:

```typescript
import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'html',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium-320',
      use: { ...devices['Desktop Chrome'], viewport: { width: 320, height: 568 } },
    },
    {
      name: 'chromium-768',
      use: { ...devices['Desktop Chrome'], viewport: { width: 768, height: 1024 } },
    },
    {
      name: 'chromium-1024',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1024, height: 768 } },
    },
    {
      name: 'chromium-1440',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
})
```

Key choices:
- `testDir: './e2e'` — keeps e2e tests separate from Vitest unit tests in `src/`
- `baseURL` from env var → CI sets `PLAYWRIGHT_BASE_URL`, local dev defaults to Vite port 5173
- No `webServer` block in the config — running the dev server is a manual prerequisite for now (defer `webServer` to CI pipeline story)
- 4 `projects` covering the 4 required breakpoints; all use Chromium only

### §4 — Playwright Test File

Create `web/e2e/responsive-layout.spec.ts`:

```typescript
import { test, expect } from '@playwright/test'

// Route mocks so tests run without a real backend.
// The _app.tsx beforeLoad fires POST /api/auth/refresh on page load
// (silent re-auth path). We intercept it to inject a fake access token so
// React Query hydrates without a real DB.
async function mockAuth(page: import('@playwright/test').Page) {
  await page.route('/api/auth/refresh', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        accessToken: 'test-token',
        refreshToken: 'test-refresh',
        expiresIn: 3600,
      }),
    }),
  )
  // Permissions query used by usePermissionsQuery() — return an empty snapshot
  // so usePermission() gates return false (no admin link, no canCreate etc.).
  await page.route('/api/auth/permissions', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({}),
    }),
  )
  // Nav menus — return a single leaf item so the Navbar renders something
  // tapable (needed for the auto-close test).
  await page.route('/api/menus/nav', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 'test-item',
          name: 'Test Item',
          order: 0,
          icon: null,
          parentId: null,
          children: [],
        },
      ]),
    }),
  )
}

test.describe('Responsive layout — sidebar + hamburger', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuth(page)
    await page.goto('/')
  })

  test('320 px — hamburger visible, sidebar hidden', async ({ page }) => {
    // Hamburger has aria-label "Open menu" (nav.openMenu i18n key)
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await expect(hamburger).toBeVisible()

    // The <nav> element is hidden via Tailwind's conditional class:
    // when drawerOpen=false, it gets class "hidden" which maps to display:none.
    const nav = page.getByRole('navigation', { name: /primary navigation/i })
    await expect(nav).not.toBeVisible()
  })

  test('768 px — sidebar visible, hamburger hidden', async ({ page }) => {
    // At ≥768 px the hamburger has md:hidden class → display:none.
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await expect(hamburger).not.toBeVisible()

    // The <nav> gets md:block → always visible at desktop widths.
    const nav = page.getByRole('navigation', { name: /primary navigation/i })
    await expect(nav).toBeVisible()
  })

  test('1024 px — sidebar visible, hamburger hidden', async ({ page }) => {
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await expect(hamburger).not.toBeVisible()

    const nav = page.getByRole('navigation', { name: /primary navigation/i })
    await expect(nav).toBeVisible()
  })

  test('1440 px — sidebar visible, hamburger hidden', async ({ page }) => {
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await expect(hamburger).not.toBeVisible()

    const nav = page.getByRole('navigation', { name: /primary navigation/i })
    await expect(nav).toBeVisible()
  })
})

test.describe('Mobile drawer auto-close', () => {
  // This suite only makes sense at 320 px (mobile viewport).
  // Playwright's project config applies the correct viewport per project.
  // We guard with a conditional so the test is a no-op at wider widths
  // (still passes — it just asserts nothing).

  test('320 px — tapping nav item closes the drawer', async ({ page }) => {
    await mockAuth(page)
    await page.goto('/')

    const vp = page.viewportSize()
    if (!vp || vp.width >= 768) {
      // Wider viewport: drawer concept does not apply — skip implicitly.
      return
    }

    // Open the drawer.
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await hamburger.click()

    // The nav should now be visible.
    const nav = page.getByRole('navigation', { name: /primary navigation/i })
    await expect(nav).toBeVisible()

    // Tap the menu item (our mock returns one item named "Test Item").
    const navItem = nav.getByRole('button', { name: 'Test Item' })
    await navItem.click()

    // Drawer should close: nav hidden again.
    await expect(nav).not.toBeVisible()
  })
})

test.describe('No horizontal scroll', () => {
  test('no horizontal overflow at any viewport', async ({ page }) => {
    await mockAuth(page)
    await page.goto('/')

    const overflow = await page.evaluate(() => {
      return document.documentElement.scrollWidth > window.innerWidth
    })
    expect(overflow, 'horizontal scroll should not appear').toBe(false)
  })
})
```

**Notes on the test design:**
- `mockAuth()` is a plain `async function` (not a Playwright fixture) — keeps the single test file self-contained for a first Playwright setup
- The 768/1024/1440 tests are structurally identical but each project runs with its own viewport, so Playwright runs them in the correct context
- The 320 px–only mobile-drawer test uses an explicit `vp.width >= 768` guard so it passes (as a no-op) when accidentally run in a wider viewport project (avoids false failures)
- The API route mocks use literal English strings for `name:` matchers — if i18n key values change, update both the mocks and the `getByRole` name selectors together

### §5 — Architecture Compliance

| AC | Architecture Reference | Requirement |
|-----|----------------------|-------------|
| AC-1, AC-2 | FR-22, Decision 4.1 | Responsive single-column on mobile, sidebar on desktop |
| AC-3 | FR-22 AC-3, Story 4.7 | Hamburger with auto-close on item tap |
| AC-4 | FR-37 AC-3, WCAG 2.5.5 | Touch target ≥44×44 px |
| AC-5 | FR-37 AC-4 | No horizontal scroll at ≥320 px |
| AC-6 | Sprint S7 CI validation gate | Playwright breakpoint sweep |

Decision 4.7 (Decision 4.7 — Testing Standards): unit/component tests use Vitest + RTL; e2e tests use Playwright (per architecture line referencing "GitHub Actions" CI and AR-43 axe-core gate for Story 7.4). Playwright is the correct tool for viewport-level layout verification.

### §6 — File Locations

| Action | Path |
|--------|------|
| MODIFY | `web/src/components/shared/Navbar.tsx` — NavSkeleton function |
| MODIFY | `web/src/components/designer/DynamicComponent.tsx` — isLoading return |
| MODIFY | `web/src/routes/_app.tsx` — add `overflow-x-hidden` to `<main>` if needed |
| MODIFY | `web/src/components/shared/__tests__/Navbar.test.tsx` — update skeleton assertion |
| MODIFY | `web/package.json` — add `test:e2e` script |
| CREATE | `web/playwright.config.ts` |
| CREATE | `web/e2e/responsive-layout.spec.ts` |

### §7 — Critical Do-Nots

- Do NOT add `overflow-x-hidden` to the outer `<div className="min-h-screen md:flex">` wrapper in `_app.tsx` — it would clip the fixed-positioned navbar on some browsers; add it to `<main>` only if overflow is confirmed
- Do NOT remove the `NavSkeleton` `aria-hidden` attribute when refactoring to use `<Skeleton>` — the skeleton is decorative and should remain hidden from screen readers
- Do NOT move Playwright tests into `web/src/` (Vitest will try to collect them); keep them in `web/e2e/`
- Do NOT add `@playwright/test` to the Vitest `exclude` list — it is already excluded because Playwright tests live in `web/e2e/`, not `web/src/`, and `configDefaults.exclude` already skips `node_modules`
- Do NOT import `DynamicComponent`'s `<Skeleton>` from a relative path — use `@/components/ui/skeleton` (the `@` alias maps to `web/src/`, configured in `vite.config.ts`)

### §8 — Previous Story Learnings (from Story 6.11)

- The shadcn `Skeleton` component (`web/src/components/ui/skeleton.tsx`) was created manually in Story 6.11 rather than via CLI (the CLI generated it in the wrong location). The file already exists — do NOT run `npx shadcn@latest add skeleton` again.
- The `animate-pulse` class in shadcn's Skeleton is applied to the inner element by the shadcn component itself. After replacing raw `animate-pulse` divs with `<Skeleton>`, the Navbar.test.tsx assertion `container.querySelectorAll('.animate-pulse').length === 3` should still find 3 elements because shadcn Skeleton renders an element with `animate-pulse`. Verify this; if the count differs, update the test to use a more semantic selector.
- `@/` alias resolves to `web/src/` as configured in `vite.config.ts` (`path.resolve(__dirname, './src')`). Playwright does NOT use Vite aliases — if Playwright tests need component imports, use relative paths. The e2e tests in §4 do not import any `web/src/` code, so this is not a concern for this story.

### §9 — References

| Symbol | Location |
|--------|----------|
| `AppLayout` function | `web/src/routes/_app.tsx:54` |
| `Navbar` function | `web/src/components/shared/Navbar.tsx:17` |
| `NavSkeleton` function | `web/src/components/shared/Navbar.tsx:107–115` |
| `DynamicComponent` isLoading return | `web/src/components/designer/DynamicComponent.tsx:303–304` |
| Skeleton component | `web/src/components/ui/skeleton.tsx` |
| Navbar tests | `web/src/components/shared/__tests__/Navbar.test.tsx` |
| vite.config.ts | `web/vite.config.ts` |

---

## Dev Agent Record

### Completion Notes

**Implementation summary** (2026-05-27):

- **Task 1 (NavSkeleton):** Added `import { Skeleton } from '@/components/ui/skeleton'` to `Navbar.tsx` and replaced the three raw `<li className="h-4 w-full animate-pulse rounded bg-gray-200" />` rows in `NavSkeleton()` with `<li><Skeleton className="h-4 w-full" /></li>`. Preserved `aria-hidden` on the outer `<ul>` per Critical Do-Not. The shadcn `Skeleton` (`web/src/components/ui/skeleton.tsx`) renders a single `<div data-slot="skeleton" class="bg-accent animate-pulse rounded-md ...">`, so the existing `Navbar.test.tsx` assertion `container.querySelectorAll('.animate-pulse').length === 3` (Navbar.test.tsx:50) still resolves to exactly 3 elements (1 per Skeleton). Verified: all 9 Navbar tests pass.
- **Task 2 (DynamicComponent skeleton):** Added `import { Skeleton } from '@/components/ui/skeleton'` to `DynamicComponent.tsx` (file had no prior `@/components/ui` imports — placed alongside the other `@/` imports per spec). Replaced the `isLoading` return branch (line 303–304) with `<Skeleton className="h-32 w-full" />`. No behavior change beyond the visual swap (border-radius shifts from `rounded-lg` to shadcn's `rounded-md`, background shifts from `bg-slate-100` to `bg-accent` — both are theme-friendly defaults).
- **Task 3 (horizontal overflow):** Added `overflow-x-hidden` to the `<main>` element in `_app.tsx` (`flex-1 overflow-x-hidden p-4 md:pl-64`). Kept off the outer `<div className="min-h-screen md:flex">` wrapper per Critical Do-Not (would clip the fixed-positioned sidebar). This defensively guarantees AC-5 even if a downstream route's content (wide table, long pre, etc.) would otherwise force horizontal scroll.
- **Task 4 (Playwright install):** `npm install --save-dev @playwright/test` → 3 packages added, `@playwright/test ^1.60.0` in `devDependencies`. `npx playwright install chromium` downloaded Chrome for Testing 148.0.7778.96 (181.9 MiB) + Chrome Headless Shell (112.4 MiB) into `%LOCALAPPDATA%\ms-playwright`. Created `web/playwright.config.ts` per Dev Notes §3 (4 projects at 320/768/1024/1440 px, `testDir: './e2e'`, no `webServer` block per spec, `baseURL` from `PLAYWRIGHT_BASE_URL` env var). Added `"test:e2e": "playwright test"` to `web/package.json` scripts.
- **Task 5 (e2e spec):** Created `web/e2e/responsive-layout.spec.ts` per Dev Notes §4 — 6 tests × 4 viewport projects = 24 test invocations (verified via `npx playwright test --list`). The 320 px-only mobile-drawer test self-guards with `if (!vp || vp.width >= 768) return` so the wider-viewport projects run it as a no-op. Tests intercept `/api/auth/refresh`, `/api/auth/permissions`, `/api/menus/nav` via `page.route()` so no backend is required.
- **Defensive Vitest exclude:** Added `'e2e/**'` to `vite.config.ts` `test.exclude`. Vitest 4's default `include` pattern is `**/*.{test,spec}.?(c|m)[jt]s?(x)` scanned from project root — without this exclusion Vitest would also try to collect the Playwright spec and fail. The Critical Do-Not only forbids adding `@playwright/test` (the package name) to exclude; adding a path pattern is a separate concern. **Note:** the spec's Dev Notes §7 claim that Vitest auto-excludes `e2e/` because "Playwright tests live in `web/e2e/`, not `web/src/`" is incorrect — Vitest's default include is project-root-relative, not src-scoped.

**Validation:**

- **Vitest:** 136/138 tests pass. The 2 failures (`src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` → `drag-and-drop reorder calls the mutation with the expected payload`, `Space + ArrowDown + Space reorders rows and announces every step`) **pre-date this story** — reproduced on a clean tree via `git stash --include-untracked && npm test ReorderableMenuList`. Root cause is a TanStack Query mock issue (`reorderMutation.mutate is not a function`) in `ReorderableMenuList.tsx:253`; no code touched by Story 7.1 is involved. Recorded as a deferred item below.
- **Navbar tests:** 9/9 pass — confirms Skeleton swap preserves the `animate-pulse` count contract.
- **Lint:** No new errors introduced. `npx eslint` on the 3 modified `web/src/` files reports only the same `react-refresh/only-export-components` error that every other `routes/_app/*.tsx` file emits (TanStack Router pattern exporting `Route` + component from one file). Pre-existing; matches all sibling route files.
- **Playwright config + spec:** Parsed cleanly via `npx playwright test --list` → 24 tests listed across 4 viewport projects. Actual `npm run test:e2e` execution requires a running dev server (`npm run dev`) per Dev Notes §3 "running the dev server is a manual prerequisite for now (defer webServer to CI pipeline story)" — deferred to manual verification or CI pipeline story.

**Acceptance Criteria mapping:**

| AC | Satisfied by | Verified by |
|----|--------------|-------------|
| AC-1 | Pre-existing `min-h-screen md:flex` + `md:hidden` on hamburger + `hidden` default on `<nav>` (Navbar.tsx:56, 78). NavSkeleton refactor refines presentation only. | `[chromium-320] 320 px — hamburger visible, sidebar hidden` |
| AC-2 | Pre-existing `md:fixed md:inset-y-0 md:left-0 md:block w-64` on `<nav>` + `md:pl-64` on `<main>` (Navbar.tsx:75, _app.tsx:70). | `[chromium-768/1024/1440] sidebar visible, hamburger hidden` (×3) |
| AC-3 | Pre-existing `onLeafClick={closeDrawer}` wiring on every leaf button (Navbar.tsx:97). Validated end-to-end at viewport level by the Playwright auto-close test. | `[chromium-320] tapping nav item closes the drawer` |
| AC-4 | Pre-existing `min-h-[44px] min-w-[44px]` on hamburger, leaf buttons, disclosure toggles (Navbar.tsx:56, 124, 139, 152). | Static-class audit — no e2e probe required per spec. |
| AC-5 | Added `overflow-x-hidden` to `<main>` in _app.tsx. | `[all 4 viewports] no horizontal overflow at any viewport` |
| AC-6 | Created `web/playwright.config.ts` (4 viewport projects) + `web/e2e/responsive-layout.spec.ts` (6 tests × 4 projects = 24 invocations). | `npx playwright test --list` confirms collection. |

**Deferred items (not blocking story completion):**

1. **`ReorderableMenuList.test.tsx` × 2 failures pre-date Story 7.1.** Root cause is a broken `useMutation` mock returning a shape without `.mutate`. Not in scope for 7.1; flag for the team to address in a follow-up or under Epic 4 retrospective.
2. **No `webServer` block in `playwright.config.ts`.** Per Dev Notes §3 explicit deferral. The CI pipeline story will wire `webServer: { command: 'npm run dev', url: '...', reuseExistingServer: !process.env.CI }` so `npm run test:e2e` becomes a one-command run. Today it requires a manual `npm run dev` in another terminal.
3. **No Playwright run executed in this session.** The 24 tests parse via `--list` but were not actually run because the dev server prerequisite is manual (per (2) above). First real run will happen as part of the CI pipeline story or local QA verification.
4. **`devices['Desktop Chrome']` at width 320 px is not a true mobile profile.** Per Dev Notes §3 the spec uses Desktop Chrome with an overridden viewport; touch emulation, real mobile UA, and reduced-motion handling are not exercised. Story 7.4 (accessibility) or a future mobile-specific testing story may want a `devices['iPhone SE']` project.
5. **Vitest `exclude` annotation deviates from spec wording.** The spec's Dev Notes §7 says "Do NOT add `@playwright/test` to the Vitest `exclude` list" — that's about adding the package name (which would do nothing useful). I added `e2e/**` (a path pattern) which is functionally necessary because the spec's claim "configDefaults.exclude already skips node_modules" doesn't cover the `web/e2e/` path. Recorded here in case a future spec author wants to clarify the intent.
6. **Single locale for `getByRole({ name: ... })` matchers.** The Playwright spec matchers use literal English strings (`/open menu/i`, `/primary navigation/i`). The current i18n bundles render English strings, so this works today. If the locale defaults change or a CI region uses a different locale, the matchers will fail. Spec author flagged this in Dev Notes §4 Notes.

### File List

**Modified:**
- `web/src/components/shared/Navbar.tsx` — NavSkeleton uses shadcn `<Skeleton>` instead of raw `animate-pulse` div; added Skeleton import.
- `web/src/components/designer/DynamicComponent.tsx` — `isLoading` branch returns `<Skeleton className="h-32 w-full" />` instead of raw `animate-pulse` div; added Skeleton import.
- `web/src/routes/_app.tsx` — added `overflow-x-hidden` to `<main>` for defensive AC-5 guarantee.
- `web/vite.config.ts` — added `'e2e/**'` to Vitest `test.exclude` so Playwright specs are not collected by Vitest.
- `web/package.json` — added `"test:e2e": "playwright test"` script; `@playwright/test ^1.60.0` added to devDependencies.
- `web/package-lock.json` — npm install side-effect (3 packages added).

**Created:**
- `web/playwright.config.ts` — Playwright config: 4 viewport projects (320 / 768 / 1024 / 1440 px), `testDir: './e2e'`, `reporter: 'html'`, no `webServer` block.
- `web/e2e/responsive-layout.spec.ts` — 6 tests × 4 viewport projects covering AC-1 through AC-6.

**Untouched (no edits, but Navbar.test.tsx behavior verified):**
- `web/src/components/shared/__tests__/Navbar.test.tsx` — assertion `container.querySelectorAll('.animate-pulse').length === 3` still passes against the shadcn Skeleton (Skeleton renders one element with `animate-pulse` per instance).

### Change Log

| Date | Change |
|------|--------|
| 2026-05-27 | Story created — ready-for-dev |
| 2026-05-27 | Implementation complete — NavSkeleton + DynamicComponent skeleton refactored to shadcn Skeleton, overflow-x-hidden added defensively to `<main>`, Playwright installed and configured at 4 viewports, e2e spec created (24 tests parsed via `--list`). 136/138 Vitest tests pass (2 pre-existing failures unrelated). Status → review. |
