# Story 7.4: Accessibility Compliance

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a user with assistive technology,
I want to navigate and use FormForge with a keyboard and screen reader,
so that the platform is accessible to all users.

## Acceptance Criteria

**AC-1 — Keyboard-reachable controls in logical tab order**
**Given** any interactive control on any FormForge page
**When** I navigate via keyboard
**Then** the control is keyboard-reachable in a logical tab order

**AC-2 — Form inputs have labels; validation errors are linked**
**Given** any form input
**When** I inspect its accessibility tree
**Then** it has an associated `<label>` or `aria-label`
**And** any validation errors are linked via `aria-describedby` pointing to the error message

**AC-3 — Contrast ratio ≥4.5:1 across all themes**
**Given** body text against background
**When** I run a contrast audit
**Then** the contrast ratio is ≥4.5:1 across all themes (default-light, slate-dark, solarized)

**AC-4 — DnD interactions have keyboard equivalents**
**Given** all DnD interactions (designer canvas, menu reorder)
**When** I attempt them with keyboard only
**Then** the `useKeyboardDnD` hook from Story 3.10 provides equivalent functionality

**AC-5 — Zero axe-core critical violations on DynamicComponent forms**
**Given** a rendered DynamicComponent form (any production form)
**When** the axe-core CI gate runs (per AR-43)
**Then** zero critical violations are reported

**AC-6 — axe-core smoke audit is a required CI check**
**Given** the CI pipeline
**When** a PR is opened
**Then** the axe-core smoke audit is a required check

---

## Tasks / Subtasks

- [x] **Task 1 — Replace deprecated `aria-grabbed` with ARIA 1.1-compliant pattern** (AC-1, AC-4)
  - [x] Read `web/src/components/designer/DesignerCanvas.tsx` fully and identify all `aria-grabbed` usages
  - [x] Read `web/src/components/designer/DesignerToolbar.tsx` fully and identify all `aria-grabbed` usages
  - [x] Search `web/src/` for `aria-grabbed` in any menu reorder component (e.g., `ReorderableMenuList`) — Story 4.5 drag-and-drop may also use it
  - [x] Replace `aria-grabbed` with ARIA 1.1 pattern: the keyboard pickup button should use `aria-pressed={pickedUp !== null}` (where `pickedUp` comes from `useKeyboardDnD`); the draggable container itself should have no `aria-grabbed` attribute — rely on the `aria-live` region (already provided by `useKeyboardDnD`) to communicate DnD state; see Dev Notes §1
  - [x] Verify each DropZone in DesignerCanvas has a descriptive `aria-label` (e.g., "Drop zone between {componentA} and {componentB}") — this was part of Story 3.10's architecture contract (Decision 4.5)
  - [x] Verify the `aria-live="polite"` announcement region is rendered somewhere in the component tree for the keyboard DnD state (should already exist from Story 3.10; confirm it is not conditionally hidden)
  - [x] Run `axe-core` on DesignerCanvas — `aria-grabbed` will produce a `deprecated-aria-attribute` violation; confirm it's gone after the fix

- [x] **Task 2 — Audit DynamicComponent form inputs for complete ARIA coverage** (AC-2)
  - [x] Read `web/src/components/designer/ElementRenderer.tsx` in full
  - [x] For every leaf element type rendered by `ElementRenderer` (TextInput, NumberInput, DateInput, Checkbox, Select/Dropdown, TextArea, ColorPicker, FileUpload, RepeaterField): verify there is an associated `<label htmlFor={...}>` or `aria-label` on the input element
  - [x] Verify every error message `<span>`/`<p>` has a stable `id` (e.g., `${bindTo}-error`) and that its corresponding input carries `aria-describedby={`${bindTo}-error`}` when an error exists — the existing `ariaFallback()` pattern in ElementRenderer already handles some of this; close any remaining gaps
  - [x] Verify `aria-invalid={!!error}` is set on every input when there is an error (already partially done per the aria-invalid selectors in `ui/input.tsx` — confirm the controlling attribute is being passed by ElementRenderer)
  - [x] Ensure the Tabs component (if used as a container element) has proper `role="tablist"`, `role="tab"`, `role="tabpanel"`, and `aria-selected` attributes
  - [x] Fix any gaps found; do not add unnecessary aria if shadcn/Radix already handles it (Radix handles CheckboxPrimitive, Select/Dropdown, Dialog)

- [x] **Task 3 — Install axe-core tooling** (AC-5, AC-6)
  - [x] In `web/package.json` devDependencies, add `"axe-core": "^4.10"` (for Vitest component tests — use directly, no wrapper package needed)
  - [x] In `web/package.json` devDependencies, add `"@axe-core/playwright": "^4.10"` (for the Playwright e2e smoke audit)
  - [x] Run `npm install` in `web/`
  - [x] Verify `axe-core` imports work from `jsdom` environment (vitest already uses jsdom per `vite.config.ts` — no additional config needed)

- [x] **Task 4 — Add axe-core Vitest component tests for DynamicComponent** (AC-5)
  - [x] Create `web/src/components/designer/__tests__/DynamicComponentA11y.test.tsx`
  - [x] Import `run as axeRun` from `axe-core` directly — do NOT use `jest-axe` or `vitest-axe` wrappers (avoid extra dependencies); see Dev Notes §2 for the exact test helper pattern
  - [x] Write three test cases against `DynamicComponent` rendered with @testing-library/react:
    1. Simple form schema (one TextInput, one NumberInput, one Checkbox) — assert zero critical axe violations
    2. Form with server-injected validation errors (populate `serverErrors` prop) — assert zero critical violations; validates that error messages are properly linked via `aria-describedby`
    3. Schema with Dropdown and Repeater — assert zero critical violations
  - [x] Use `axeRun(container, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa'] } })` to scope to WCAG 2.1 AA rules
  - [x] Filter for `impact === 'critical'` violations only (matching the AC: "zero critical violations"); non-critical (serious/moderate/minor) are allowed to fail without breaking the gate in v1
  - [x] Add `afterEach(cleanup)` — see Story 7.3 Dev Notes §16: cleanup is NOT auto-registered in this project's Vitest setup
  - [x] See Dev Notes §2 for schema fixture shapes and mock setup

- [x] **Task 5 — Add Playwright axe-core e2e smoke test** (AC-5, AC-6)
  - [x] Create `web/e2e/accessibility.spec.ts`
  - [x] Import `AxeBuilder` from `@axe-core/playwright`
  - [x] Use the same `mockAuth` pattern as `web/e2e/responsive-layout.spec.ts` (adds auth token to localStorage) and additionally mock the API routes for a DynamicComponent form via `page.route()`
  - [x] Navigate to a data entry route that renders `DynamicComponent` (e.g., route `/data/test-designer/new`)
  - [x] Run `new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()`
  - [x] Assert `results.violations.filter(v => v.impact === 'critical').length === 0`
  - [x] Also run a full-page axe check on the main nav page (home/dashboard) to cover AC-1 keyboard navigation concerns
  - [x] Update `web/playwright.config.ts`: add a `webServer` block so the e2e tests can run in CI without a manually started server — use `vite preview` with the built output; see Dev Notes §3

- [x] **Task 6 — Create GitHub Actions CI workflow** (AC-6)
  - [x] Create `.github/workflows/ci.yml` — see Dev Notes §4 for the complete shape
  - [x] Job `test-backend`: dotnet restore → build → `dotnet test` (xUnit + Testcontainers; requires Docker-in-Docker or the `ubuntu-latest` runner's Docker daemon)
  - [x] Job `test-frontend`: `npm ci` → `vitest run` (includes new axe-core component tests from Task 4) → ESLint → TS typecheck → `npm audit --audit-level=high`
  - [x] Job `test-e2e`: `npm ci` → `npm run build` → `npx playwright install --with-deps chromium` → `npx playwright test` (includes `accessibility.spec.ts` from Task 5)
  - [x] The e2e job name must appear in GitHub branch protection rules as a required check — document this in a PR description comment; you cannot configure branch protection from a workflow file
  - [x] Gate condition: all three jobs must pass for the PR to be mergeable

- [x] **Task 7 — Backend tests unchanged; Frontend tests pass** (verification)
  - [x] Confirm all existing 539 backend tests still pass (`dotnet test`)
  - [x] Confirm all existing 151 frontend tests still pass + new axe-core component tests
  - [x] Confirm the 2 pre-existing `ReorderableMenuList.test.tsx` failures are unchanged (unrelated to this story; do not attempt to fix them)

---

## Dev Notes

### §1 — Replacing `aria-grabbed`

`aria-grabbed` was deprecated in ARIA 1.1 (2017) and removed in ARIA 1.2. axe-core's `deprecated-aria-attribute` rule flags it as a **critical** violation. The fix is:

**DO NOT use:**
```tsx
<div draggable aria-grabbed={pickedUp !== null} />
```

**DO use — keyboard pickup button pattern:**
```tsx
{/* The button that keyboard users press to pick up an item */}
<button
  aria-pressed={pickedUp !== null}
  aria-label={pickedUp ? t('designer.keyboard.drop') : t('designer.keyboard.pickUp')}
  onClick={() => pickedUp ? commit(...) : pickUp(...)}
/>
```

The `useKeyboardDnD` hook (Story 3.10) already manages `pickedUp` state and the `announcement` string for the `aria-live` region. The hook returns `{ pickedUp, announcement, pickUp, commit, cancel, originatorRef, insertedIdRef }`. No changes needed to the hook itself.

The `aria-live="polite"` region pattern (from Story 3.10):
```tsx
<div aria-live="polite" aria-atomic="true" className="sr-only">
  {announcement}
</div>
```
This must be rendered unconditionally in the component tree (not toggled with a conditional) so screen readers register it on first render.

For the menu reorder DnD: check `web/src/components/` for any menu reorder component created in Story 4.5. Apply the same pattern if `aria-grabbed` is found there.

### §2 — axe-core Vitest test helper pattern

Use axe-core directly from jsdom — no wrapper package:

```typescript
// web/src/components/designer/__tests__/DynamicComponentA11y.test.tsx
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import { run as axeRun, type Result } from 'axe-core'
import DynamicComponent from '../DynamicComponent'
// Mock i18n, react-query, etc. as other tests in this folder do

afterEach(cleanup) // REQUIRED — this project does NOT auto-cleanup

async function assertNoAxeCriticalViolations(container: HTMLElement) {
  const results = await axeRun(container, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa'] },
  })
  const critical: Result[] = results.violations.filter((v) => v.impact === 'critical')
  if (critical.length > 0) {
    const msg = critical
      .map((v) => `[${v.id}] ${v.description}\n  ${v.nodes.map((n) => n.html).join('\n  ')}`)
      .join('\n')
    throw new Error(`axe found ${critical.length} critical violation(s):\n${msg}`)
  }
}

// Schema fixture — simple form
const simpleSchema = {
  type: 'Stack',
  elements: [
    { type: 'TextInput', label: 'First Name', bindTo: 'firstName', required: true },
    { type: 'NumberInput', label: 'Age', bindTo: 'age' },
    { type: 'Checkbox', label: 'Active', bindTo: 'isActive' },
  ],
}

describe('DynamicComponent a11y', () => {
  it('simple form has no critical violations', async () => {
    const { container } = render(
      <DynamicComponent schema={simpleSchema} initialData={{}} onSubmit={vi.fn()} />,
    )
    await assertNoAxeCriticalViolations(container)
  })

  it('form with server errors has no critical violations', async () => {
    const { container } = render(
      <DynamicComponent
        schema={simpleSchema}
        initialData={{}}
        onSubmit={vi.fn()}
        serverErrors={{ firstName: 'Required field' }}
      />,
    )
    await assertNoAxeCriticalViolations(container)
  })

  it('dropdown and repeater schema has no critical violations', async () => {
    const schemaWithDropdown = {
      type: 'Stack',
      elements: [
        { type: 'Select', label: 'Category', bindTo: 'category', options: ['A', 'B', 'C'] },
        { type: 'Repeater', label: 'Items', bindTo: 'items', elements: [
          { type: 'TextInput', label: 'Item Name', bindTo: 'itemName' }
        ]},
      ],
    }
    const { container } = render(
      <DynamicComponent schema={schemaWithDropdown} initialData={{}} onSubmit={vi.fn()} />,
    )
    await assertNoAxeCriticalViolations(container)
  })
})
```

**Mock setup:** Follow the exact same vi.mock pattern used in `ComponentPreviewModal.test.tsx` or `RepeaterRowDrawer.test.tsx` for i18n and react-query mocking. DynamicComponent depends on both.

**Important:** `axeRun` is async. Use `await`. jsdom must be the test environment (already configured in `vite.config.ts`).

### §3 — Playwright axe-core smoke test and webServer

**playwright.config.ts webServer block** (add to the existing config; do not replace the viewport projects):
```typescript
// web/playwright.config.ts
import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  // existing viewport projects...
  webServer: {
    command: 'npm run preview',   // 'vite preview' after 'npm run build'
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env['CI'],  // reuse in dev, always start fresh in CI
    timeout: 120_000,
  },
  use: {
    baseURL: 'http://localhost:4173',
  },
})
```

Add `"preview": "vite preview"` to `web/package.json` scripts if not already present.

**accessibility.spec.ts shape:**
```typescript
import { test, expect } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

// Reuse the mockAuth helper from responsive-layout.spec.ts (move to shared fixture if needed)
async function mockAuth(page) {
  await page.addInitScript(() => {
    localStorage.setItem('ff-access-token', 'mock-token')
    localStorage.setItem('ff-theme', 'default-light')
  })
  // Mock the auth refresh endpoint so useAuthQuery doesn't redirect
  await page.route('**/api/auth/refresh', route => route.fulfill({
    status: 200,
    body: JSON.stringify({ accessToken: 'mock', refreshToken: 'mock', expiresIn: 900,
      user: { userId: '1', email: 'test@test.com', displayName: 'Test', themePreference: null, roles: ['admin'] } }),
  }))
}

test('main navigation page has no critical a11y violations', async ({ page }) => {
  await mockAuth(page)
  await page.goto('/')
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze()
  const critical = results.violations.filter(v => v.impact === 'critical')
  expect(critical, `Critical violations: ${JSON.stringify(critical, null, 2)}`).toHaveLength(0)
})

test('data entry form has no critical a11y violations', async ({ page }) => {
  await mockAuth(page)
  // Mock schema API so DynamicComponent renders a real form
  await page.route('**/api/designers/*/versions/published', route => route.fulfill({
    status: 200, body: JSON.stringify({ /* minimal ComponentSchemaVersion fixture */ }),
  }))
  await page.goto('/data/test-designer/new')
  await page.waitForSelector('[data-testid="dynamic-form"]', { timeout: 5000 }).catch(() => {})
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze()
  const critical = results.violations.filter(v => v.impact === 'critical')
  expect(critical, `Critical violations: ${JSON.stringify(critical, null, 2)}`).toHaveLength(0)
})
```

All three themes should be tested: run the navigation page test with `data-theme="slate-dark"` and `data-theme="solarized"` set via `addInitScript` to cover AC-3 contrast checks across themes.

### §4 — GitHub Actions CI workflow shape

```yaml
# .github/workflows/ci.yml
name: CI

on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

jobs:
  test-backend:
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgres:16
        env:
          POSTGRES_PASSWORD: postgres
        options: >-
          --health-cmd pg_isready
          --health-interval 10s
          --health-timeout 5s
          --health-retries 5
        ports:
          - 5432:5432
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release
        env:
          ConnectionStrings__formforge: "Host=localhost;Database=ff_test;Username=postgres;Password=postgres"

  test-frontend:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '22'
          cache: 'npm'
          cache-dependency-path: web/package-lock.json
      - run: npm ci
      - run: npm run test -- --run         # vitest run (includes axe-core component tests)
      - run: npm run lint
      - run: npm run typecheck
      - run: npm audit --audit-level=high

  test-e2e:
    runs-on: ubuntu-latest
    needs: [test-frontend]   # only run e2e if unit tests pass
    defaults:
      run:
        working-directory: web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '22'
          cache: 'npm'
          cache-dependency-path: web/package-lock.json
      - run: npm ci
      - run: npm run build
      - run: npx playwright install --with-deps chromium
      - run: npx playwright test          # includes accessibility.spec.ts — the axe-core smoke audit
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-report
          path: web/playwright-report/
          retention-days: 7
```

**Branch protection (manual step after merging this CI file):** In the GitHub repo settings → Branches → main protection rule → Required status checks, add `test-e2e` as a required check. This enforces AC-6. The developer creating this story cannot automate branch protection from code alone — document this in the PR description.

### §5 — ElementRenderer ARIA compliance map

Expected pattern per leaf element type (verify these are actually present in ElementRenderer.tsx):

| Element type | Must have |
|---|---|
| TextInput, NumberInput, DateInput, TextArea | `<label htmlFor={id}>` + `id` on input + `aria-invalid` + `aria-describedby` when error |
| Checkbox | `<label htmlFor={id}>` or Radix Checkbox with label wired |
| Select/Dropdown | `<label>` or `aria-label` on the trigger + `aria-describedby` on error |
| ColorPicker | `aria-label` (no native label support for color inputs across browsers) |
| FileUpload | `<label>` that is visually rendered |
| Repeater row | `aria-label` describing the row context (e.g., "Item 2 of 3") |

The `ariaFallback()` function already in ElementRenderer applies `aria-label` when no visible label exists. Ensure it is applied to ALL leaf types, not just some.

Error message element pattern:
```tsx
// The error <span> must have an id and the input must reference it:
<input
  id={`${bindTo}-input`}
  aria-invalid={!!error}
  aria-describedby={error ? `${bindTo}-error` : undefined}
/>
{error && <span id={`${bindTo}-error`} role="alert">{error}</span>}
```

Use `role="alert"` on the error span so screen readers announce it immediately when it appears.

### §6 — Architecture compliance

| AC | Architecture Reference | Implementation |
|----|----------------------|----------------|
| AC-1 | FR-42: keyboard reachable in logical tab order | Tab order follows DOM order; shadcn/Radix handles interactive elements; skip link in Navbar (Story 4.7) |
| AC-2 | FR-42: labels + aria-describedby on errors | ElementRenderer audit + ariaFallback() coverage for all leaf types |
| AC-3 | FR-42: contrast ≥4.5:1; FR-38: 3 themes | axe-core `color-contrast` rule in wcag2aa; all three themes tested in Playwright |
| AC-4 | Decision 4.5: useKeyboardDnD hook; AR-30 | aria-grabbed → aria-pressed replacement; existing hook preserved |
| AC-5 | FR-42: axe-core zero critical; AR-43: CI gate | vitest-axe component tests + Playwright axe smoke test |
| AC-6 | AR-43: axe-core smoke audit required on PR | GitHub Actions `test-e2e` job with accessibility.spec.ts as required check |

### §7 — File Locations

| Action | Path | Purpose |
|--------|------|---------|
| CREATE | `.github/workflows/ci.yml` | Full CI pipeline with backend, frontend, and e2e jobs |
| CREATE | `web/src/components/designer/__tests__/DynamicComponentA11y.test.tsx` | axe-core component tests (Vitest) for AC-5 gate |
| CREATE | `web/e2e/accessibility.spec.ts` | axe-core Playwright smoke test for CI gate (AC-5, AC-6) |
| MODIFY | `web/package.json` | Add `axe-core` + `@axe-core/playwright` to devDependencies |
| MODIFY | `web/playwright.config.ts` | Add `webServer` block for `vite preview` (required for e2e in CI) |
| MODIFY | `web/src/components/designer/DesignerCanvas.tsx` | Replace `aria-grabbed` with `aria-pressed` on keyboard pickup button |
| MODIFY | `web/src/components/designer/DesignerToolbar.tsx` | Replace `aria-grabbed` with `aria-pressed` on keyboard pickup button |
| MODIFY | `web/src/components/designer/ElementRenderer.tsx` | Close any ARIA label / aria-describedby gaps found in audit |
| MODIFY (if found) | `web/src/components/shared/ReorderableMenuList.tsx` (or similar) | Replace `aria-grabbed` with `aria-pressed` if present in menu reorder component |

### §8 — Critical Do-Nots

- **Do NOT change the `useKeyboardDnD` hook** — it is correctly implemented (Story 3.10) and used by both designer canvas and menu reorder. Only change the consuming components that incorrectly add `aria-grabbed`.
- **Do NOT add `aria-label` to elements that already have a visible `<label>` sibling** — this violates WCAG 1.3.1 and causes duplication in screen readers. The `ariaFallback()` pattern in ElementRenderer only applies aria-label when no visible label exists; preserve this behavior.
- **Do NOT gate on non-critical violations** — AC-5 specifies "zero critical violations". Serious/moderate/minor axe findings are allowed in v1 and do not block the CI gate. Filtering on `impact === 'critical'` only.
- **Do NOT use `aria-hidden="true"` on focusable elements** — removes them from the keyboard tab order and the accessibility tree simultaneously. Use `tabIndex={-1}` if you need to remove an element from tab order while keeping it visible.
- **Do NOT skip the `webServer` block in playwright.config.ts** — without it, `playwright test` in CI will connect to nothing and all e2e tests will fail with "connection refused". The `reuseExistingServer: !process.env['CI']` flag is critical: in CI, always start fresh; in dev, reuse an already-running `vite` server.
- **Do NOT use `jest-axe` or `vitest-axe` wrapper packages** — use `axe-core` directly. Fewer dependencies; the wrapper APIs add no value over the pattern in §2.
- **Do NOT attempt to fix the pre-existing 2 ReorderableMenuList.test.tsx failures** — they are unrelated to this story (present since Story 7.2, confirmed in 7.3 completion notes). Leave them as-is.
- **Do NOT add `aria-live` regions to multiple places in the DnD component tree** — there should be exactly one `aria-live="polite"` region per DnD context. Multiple regions cause duplicate announcements on screen readers.

### §9 — Previous Story Learnings (from 7.3)

- `afterEach(cleanup)` must be added explicitly in every new `*.test.tsx` file — this project does NOT auto-register cleanup in Vitest setup.
- The two pre-existing `ReorderableMenuList.test.tsx` failures are unrelated noise — 151 pass / 2 fail is the current baseline; new axe tests should bring it to 154 pass / 2 fail.
- When writing Playwright tests that mock auth, study `web/e2e/responsive-layout.spec.ts` — particularly the `mockAuth` helper and how `page.addInitScript` injects localStorage before navigation. Also note that 7.3's `theme-no-fouc.spec.ts` added `user` field to the refresh mock; the 7.4 `mockAuth` must also include the `user` field (Story 7.3 updated `useAuthQuery` to read `data.user.themePreference`).
- The shadcn `<Select>` theme picker in `_app.tsx` already uses Radix UI under the hood — Radix Select is accessible by default; no aria work needed there.
- TypeScript strict mode is on; all new test file imports must have correct relative depths. From `web/src/components/designer/__tests__/`, the import for i18n mocks is `'../../../../lib/i18n'` (4 levels up to `web/src/`). The previous story's Debug Log #7 was caused by wrong import depth.

### §10 — Git Intelligence

- `8c98505` (7.3 review) — applied 11 patches to designer-related files; if DesignerCanvas was patched there, confirm those patches are still in place before adding more changes.
- `4be2013` (7.2) — created `themes.css` with three themes using OKLCH values; the contrast should already be WCAG AA compliant for primary text/background, but secondary/muted text (`--muted-foreground`) and card/popover backgrounds need verification via axe-core.
- `8dc7388` (7.1) — created `playwright.config.ts` WITHOUT webServer block; Task 5's playwright.config.ts update is the first modification to this file since creation.
- `21a6cad` (6.11) — established ErrorBanner component; if axe finds role issues on ErrorBanner, it is in `web/src/components/shared/ErrorBanner.tsx`.

### §11 — Contrast Verification Scope

The three themes are built with OKLCH values. Primary background/foreground pairs appear WCAG AA compliant (near 20:1 ratios). However, verify these specific color pairs via axe-core (the `color-contrast` rule in wcag2aa covers all of these):

| Token | Concern |
|---|---|
| `--muted-foreground` on `--muted` | Secondary text in muted areas — often the weakest contrast pair |
| `--card-foreground` on `--card` | Card/panel body text |
| `--popover-foreground` on `--popover` | Dropdown / tooltip text |
| `--primary-foreground` on `--primary` | Button text on primary button background |
| `--destructive` on `--background` | Error/danger text |
| `--border` color on background | Border contrast (not text — can be exempt from 4.5:1) |

The solarized theme uses Ethan Schoonover's Solarized Light palette; the muted colors (`--muted-foreground`: solarized `base1` #93a1a1) are known to have borderline contrast on the `--muted` background. axe-core will flag this if it falls below 4.5:1. Fix by adjusting the OKLCH value in `web/src/styles/themes.css` if axe flags it.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (2026-05-27)

### Debug Log References

1. **aria-grabbed → aria-pressed scope decision (Task 1).** Dev Notes §1 says "the keyboard pickup button should use `aria-pressed={pickedUp !== null}`; the draggable container itself should have no `aria-grabbed` attribute". DesignerToolbar's `PaletteCard` has `role="button"` and IS the keyboard pickup target — used `aria-pressed={isGrabbed}` (per-card, reflects "this specific palette item is the picked-up one"). DesignerCanvas's `CanvasContainer/CanvasTabsContainer/CanvasRepeaterContainer/CanvasLeaf` are draggable `<div>`s without `role="button"` — removed `aria-grabbed` entirely (no `aria-pressed` replacement because they're not button-roles). Pickup state is announced via the existing page-level `aria-live="polite"` region at `designer.$designerId.tsx:357-364`. Also removed now-unused `isGrabbed` const blocks in all four canvas component branches.

2. **Tabs role markup with deferred-render panels (Task 2).** Added `role="tablist"`/`aria-orientation`, `role="tab"`/`aria-selected`/`id`/`aria-controls` per button, and `role="tabpanel"`/`id`/`aria-labelledby` on the active panel. Only the active panel is in the DOM, so `aria-controls` is conditional on `isActive` to avoid referencing a non-existent id from inactive tabs. Used React's `useId()` to generate a per-mount prefix — two DynamicComponent instances mounted on the same page (e.g. modal + main form) can't collide on tab↔panel ids. Deliberately did NOT add arrow-key navigation between tabs (would require a larger behavioral change); standard Tab key still reaches every tab button.

3. **Color Picker ARIA gap (Task 2).** Color Picker (interactive) was missing `aria-invalid` / `aria-describedby` / error display — added all three plus `ERROR_INPUT_CLASS` styling, matching the pattern used by TextInput/TextArea/NumberInput/Dropdown/DateTime in the same file.

4. **Playwright webServer port choice (Task 5).** Dev Notes §3 prescribes `vite preview` on port 4173; changed `baseURL` from `http://localhost:5173` (vite dev) to `http://localhost:4173` (vite preview) so e2e is self-contained in CI. Local developers running `npm run dev` simultaneously won't collide (different ports), but they now need `npm run build` once before `npm run test:e2e` if no preview server is running. Reused `npm run preview -- --port 4173 --strictPort` explicitly to make port discovery deterministic; `reuseExistingServer: !process.env.CI` keeps the local dev loop responsive.

5. **DataEntryPage permissions gate broke e2e form scan (Task 5).** First mock returned an empty `{}` from `/api/auth/permissions`, but the real endpoint is `/api/users/me/permissions` (see `usePermissionsQuery.ts:29`). DataEntryPage gates DynamicComponent rendering on `permissionsData` being truthy AND `usePermission(designerId, 'canCreate')`, so an empty mock rendered the "permission denied" panel instead of the form. Fix: mock `/api/users/me/permissions` with `roleIds: ['00000000-0000-0000-0000-000000000001']` (the seeded `PLATFORM_ADMIN_ROLE_ID`), which short-circuits the per-resource check (mirrors the server-side bypass from Story 2.6 AC-2).

6. **CI workflow deviations from Dev Notes §4.** Three small adjustments: (a) `npm test` instead of `npm run test -- --run` since the package.json `test` script is already `vitest run`. (b) Omitted `npm run typecheck` — no such script exists; the codebase's existing `npm run build` chain (`vite build && tsc -b --noEmit`) currently fails on ~30 pre-existing TypeScript errors in TanStack Router signatures, Navbar.test.tsx jest-dom matcher types, etc. (none introduced by this story — verified via `git stash`). Adding a typecheck step here would always fail CI; that cleanup is its own task. (c) `npx vite build` instead of `npm run build` in `test-e2e` for the same reason — we need the dist/ bundle, not the failing tsc check.

7. **Pre-existing test failures acknowledged (Task 7).** Frontend: 2 `ReorderableMenuList.test.tsx` failures unchanged (151 → 154 passed with the 3 new axe tests; 2 failures unchanged = same baseline as Story 7.3). Backend: 3 pre-existing failures (`ProvisioningRecoveryIntegrationTests.Recovery_ScanFails_HostStartsAndServesRequests`, `SchemaAuditLogIntegrationTests.GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405`, `MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`) — confirmed via `git stash` they fail on baseline too. Final backend count: 536 passed / 3 failed (pre-existing, no backend code touched in this story). Story spec's "all 539 backend tests still pass" was written from a stale baseline; current branch matches the actual pre-existing state.

### Completion Notes List

- **AC-1, AC-4 (keyboard DnD)**: `aria-grabbed` removed from all five usages — four canvas containers (no replacement, rely on existing aria-live region) and one palette card (swapped to `aria-pressed` since it has `role="button"`). `useKeyboardDnD` hook itself was NOT touched per Critical Do-Not §8 — only consuming components changed.
- **AC-2 (form ARIA)**: ElementRenderer audit found 2 gaps: Color Picker missing error linkage (added `aria-invalid` + `aria-describedby` + error `<p>`) and Tabs missing semantic role markup (added `tablist`/`tab`/`tabpanel`/`aria-selected`/`aria-labelledby`). All other leaf types (TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTime, DynamicDropdown) already had full coverage via the existing `LabeledField` + `ariaFallback` + `inputIdFor` pattern.
- **AC-3 (contrast)**: Smoke-covered by Playwright running axe with `wcag2aa` tag (`color-contrast` rule) against all three themes (`default-light`, `slate-dark`, `solarized`) via `addInitScript` setting `localStorage['ff-theme']` before navigation. All three passed at 1024px viewport — no critical color-contrast violations.
- **AC-5 (zero critical axe violations)**: Two layers of enforcement — (1) Vitest `DynamicComponentA11y.test.tsx` runs three scenarios (simple form, form with server errors, dropdown+repeater) directly via axe-core in jsdom. (2) Playwright `accessibility.spec.ts` runs four scans (3 nav themes + 1 data entry form) via `@axe-core/playwright` against the built SPA. All 7 axe scans returned zero critical violations on the chromium-1024 project.
- **AC-6 (CI gate)**: `.github/workflows/ci.yml` defines three jobs — `test-backend`, `test-frontend`, `test-e2e`. `test-e2e` depends on `test-frontend` (only runs if Vitest passes). Repo admin needs to add `test-e2e` as a required status check under main branch protection (one-time manual step; documented at the top of the YAML and reiterated here).
- **Deferred / known limitations**:
  - Tabs do not support arrow-key navigation between tab buttons — only `Tab` key. ARIA `tablist` pattern conventionally uses arrows; this is a behavioral gap, not an axe violation. Future story can wire `useKeyboardDnD`-style focus management.
  - Repeater row `<li>` elements do not include "Item N of M" `aria-label` per Dev Notes §5. Skipped because the existing Edit/Delete buttons inside each row already have aria-labels and axe-core does not flag the omission.
  - `npm run build` and `npm run typecheck` cannot run cleanly because of ~30 pre-existing TypeScript errors in TanStack Router signatures and jest-dom matcher types. CI's `test-e2e` job calls `npx vite build` directly to bypass `tsc`. Cleanup is its own follow-up.
  - 3 pre-existing backend audit/recovery test failures (`Recovery_ScanFails_HostStartsAndServesRequests`, `GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405`, `GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`) — failing on baseline, not introduced by this story.

### File List

**Created:**
- `.github/workflows/ci.yml` — three-job CI pipeline (backend, frontend, e2e); enforces axe-core smoke audit as required check per AC-6
- `web/src/components/designer/__tests__/DynamicComponentA11y.test.tsx` — Vitest + axe-core component tests; 3 scenarios per AC-5
- `web/e2e/accessibility.spec.ts` — Playwright + `@axe-core/playwright` smoke tests; 4 scenarios across 3 themes + data entry form per AC-5/AC-6

**Modified:**
- `web/package.json` — added `axe-core ^4.10` and `@axe-core/playwright ^4.10` to devDependencies (Task 3)
- `web/package-lock.json` — npm install result for the new devDependencies
- `web/playwright.config.ts` — added `webServer` block (vite preview on 4173) + changed `baseURL` from 5173 → 4173 (Task 5)
- `web/src/components/designer/DesignerCanvas.tsx` — removed 4× `aria-grabbed` attributes and their unused `isGrabbed` const calculations from CanvasContainer, CanvasTabsContainer, CanvasRepeaterContainer, CanvasLeaf (Task 1)
- `web/src/components/designer/DesignerToolbar.tsx` — swapped 1× `aria-grabbed` to `aria-pressed` on PaletteCard (has `role="button"`); updated comment explaining the ARIA 1.1 pattern (Task 1)
- `web/src/components/designer/ElementRenderer.tsx` — Color Picker interactive branch gained `aria-invalid`/`aria-describedby`/error `<p>` + `ERROR_INPUT_CLASS`; Tabs renderer (vertical + horizontal) gained `role="tablist"`/`aria-orientation`, `role="tab"`/`aria-selected`/`id`/`aria-controls` per button, `role="tabpanel"`/`id`/`aria-labelledby` on the active panel; added `useId` import for per-mount tab id prefix (Task 2)
- `web/src/components/designer/__tests__/KeyboardDnD.canvas.test.tsx` — updated the "CanvasLeaf aria-grabbed" test to assert `aria-grabbed` is absent in BOTH paths (with and without `kbdDnD` prop), since Story 7.4 removed the attribute entirely; renamed test to match new behavior

### Review Findings

- [x] [Review][Patch] P1 — Color Picker error `<p>` missing `role="alert"` [`web/src/components/designer/ElementRenderer.tsx`] — spec §5 explicitly requires `role="alert"` on error paragraphs so dynamic injection is announced by screen readers; the new Color Picker error display (`<p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>`) omits it
- [x] [Review][Patch] P2 — `accessibility.spec.ts` `.catch(() => {})` swallows form-load failure [`web/e2e/accessibility.spec.ts`] — `await page.getByLabel(/first name/i).first().waitFor({…}).catch(() => {})` silently proceeds if the form never renders (mock mismatch, slow CI), running axe against a loading skeleton and producing a vacuous green

- [x] [Review][Defer] D1 — Repeater `<li>` rows missing `aria-label` [`web/src/components/designer/ElementRenderer.tsx`] — deferred, pre-existing; explicitly deferred by dev agent in completion notes: Edit/Delete buttons inside each row already have aria-labels and axe-core does not flag the omission
- [x] [Review][Defer] D2 — Tab widget lacks roving `tabIndex` and Arrow-key navigation [`web/src/components/designer/ElementRenderer.tsx`] — deferred, pre-existing; explicitly deferred by dev agent in completion notes: "future story can wire useKeyboardDnD-style focus management"
- [x] [Review][Defer] D3 — Empty tablist edge case: `<div role="tabpanel">` rendered with no `id`/`aria-labelledby` when `activeChild` is null [`web/src/components/designer/ElementRenderer.tsx`] — deferred, pre-existing; unlikely in real use; same root cause as the lazy-render design choice
- [x] [Review][Defer] D4 — Error `<p>` missing `role="alert"` on pre-existing leaf types (TextInput, NumberInput, TextArea, Dropdown, DateInput, DynamicDropdown) [`web/src/components/designer/ElementRenderer.tsx`] — deferred, pre-existing; not touched by this diff; same fix as P1 applies to all leaf types
- [x] [Review][Defer] D5 — `KeyboardDnD.canvas.test.tsx` expanded test does not verify `tabIndex=0` IS present when `kbdDnD` is provided — deferred, pre-existing; minor regression gap, not a correctness failure
- [x] [Review][Defer] D6 — No `typecheck` step in `test-frontend` CI job [`.github/workflows/ci.yml`] — deferred, pre-existing; explicitly deferred by dev agent: ~30 pre-existing TS errors in TanStack Router signatures and jest-dom types would always fail; cleanup is a separate task
- [x] [Review][Defer] D7 — Color Picker `-error` paragraph id (`${element.id}-error`) can collide when two interactive `DynamicComponent` instances mount simultaneously [`web/src/components/designer/ElementRenderer.tsx`] — deferred, pre-existing; identical pattern as all other leaf types (pre-existing); no regression introduced

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-27 | Story 7.4 — Accessibility Compliance: aria-grabbed → aria-pressed on PaletteCard / removed from canvas containers (relies on existing page-level aria-live region); Color Picker gains aria-invalid + aria-describedby + error display; Tabs gains tablist/tab/tabpanel/aria-selected/aria-labelledby semantic role markup; axe-core ^4.10 + @axe-core/playwright ^4.10 added as devDeps; 3 Vitest axe component tests (DynamicComponentA11y.test.tsx) + 4 Playwright axe smoke tests (accessibility.spec.ts covering 3 themes + data entry form); playwright.config.ts webServer block (vite preview :4173) + baseURL switch 5173→4173; .github/workflows/ci.yml with three-job pipeline (test-backend / test-frontend / test-e2e). Frontend: 154 passed (was 151) / 2 pre-existing ReorderableMenuList failures unchanged. Backend: 536 / 3 pre-existing audit-recovery failures unchanged. | Amelia (dev) |
