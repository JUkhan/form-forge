// Story 7.4 AC-5 / AC-6: axe-core smoke audit running over the real built
// SPA. Covers two routes — the main dashboard nav (AC-1 keyboard tab order
// implications) and a data entry route rendering a DynamicComponent form
// (AC-2 ARIA + AC-5 zero criticals).
//
// AC-3 contrast across all three themes is exercised by re-running the
// dashboard scan once per theme via addInitScript on localStorage. axe's
// color-contrast rule is part of the wcag2aa tag set we already select.
//
// Why filter `impact === 'critical'`: the AC explicitly scopes the v1 gate
// to criticals; serious/moderate/minor are surfaced for follow-up but do
// not block the CI gate.
import { test, expect, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

// Shared auth/menu mocks — kept aligned with responsive-layout.spec.ts so
// the SPA hydrates without a real backend. The /api/auth/refresh response
// must include the `user` field; Story 7.3 made useAuthQuery read
// data.user.themePreference.
async function mockAuth(page: Page) {
  await page.route('/api/auth/refresh', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        accessToken: 'test-token',
        refreshToken: 'test-refresh',
        expiresIn: 3600,
        user: {
          userId: '00000000-0000-0000-0000-000000000001',
          email: 'test@example.com',
          displayName: 'Test User',
          themePreference: null,
          roles: [],
        },
      }),
    }),
  )
  // The real endpoint is `/api/users/me/permissions` (Story 2.6+). The page
  // gates DynamicComponent on `permissionsData` being truthy AND
  // `usePermission(designerId, 'canCreate')` being true; granting the
  // platform-admin role short-circuits the per-resource check (mirrors the
  // server-side bypass in Story 2.6 AC-2).
  await page.route('**/api/users/me/permissions', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        userId: '00000000-0000-0000-0000-000000000001',
        computedAt: '2026-05-27T00:00:00Z',
        isActive: true,
        perResource: {},
        roleIds: ['00000000-0000-0000-0000-000000000001'],
      }),
    }),
  )
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

async function runAxe(page: Page): Promise<ReturnType<AxeBuilder['analyze']>> {
  return new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze()
}

function assertNoCritical(results: Awaited<ReturnType<AxeBuilder['analyze']>>) {
  const critical = results.violations.filter((v) => v.impact === 'critical')
  expect(
    critical,
    `Critical axe violations:\n${JSON.stringify(critical, null, 2)}`,
  ).toHaveLength(0)
}

test.describe('axe-core smoke — main nav (AC-1, AC-3, AC-5, AC-6)', () => {
  test('default-light theme — no critical violations', async ({ page }) => {
    await page.addInitScript(() => {
      window.localStorage.setItem('ff-theme', 'default-light')
    })
    await mockAuth(page)
    await page.goto('/')
    // Settle: wait for the navbar's primary navigation landmark to be in the
    // accessibility tree before scanning. Without it, axe runs against the
    // initial loading skeleton and may flag transient nodes.
    await page
      .getByRole('navigation', { name: /primary navigation/i })
      .or(page.getByRole('button', { name: /open menu/i }))
      .first()
      .waitFor({ state: 'attached', timeout: 5_000 })
    assertNoCritical(await runAxe(page))
  })

  test('slate-dark theme — no critical violations', async ({ page }) => {
    await page.addInitScript(() => {
      window.localStorage.setItem('ff-theme', 'slate-dark')
    })
    await mockAuth(page)
    await page.goto('/')
    await page
      .getByRole('navigation', { name: /primary navigation/i })
      .or(page.getByRole('button', { name: /open menu/i }))
      .first()
      .waitFor({ state: 'attached', timeout: 5_000 })
    assertNoCritical(await runAxe(page))
  })

  test('solarized theme — no critical violations', async ({ page }) => {
    await page.addInitScript(() => {
      window.localStorage.setItem('ff-theme', 'solarized')
    })
    await mockAuth(page)
    await page.goto('/')
    await page
      .getByRole('navigation', { name: /primary navigation/i })
      .or(page.getByRole('button', { name: /open menu/i }))
      .first()
      .waitFor({ state: 'attached', timeout: 5_000 })
    assertNoCritical(await runAxe(page))
  })
})

test.describe('axe-core smoke — DynamicComponent data entry form (AC-2, AC-5)', () => {
  test('rendered form has no critical violations', async ({ page }) => {
    await mockAuth(page)
    // The published-schema GET — return a minimal Stack with three leaves
    // covering the leaf types Task 4 audits (text/number/checkbox). Matches
    // both `/api/designers/test-designer` and the versioned form.
    await page.route('**/api/designers/test-designer**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          designerId: 'test-designer',
          displayName: 'A11y Test Form',
          status: 'Published',
          latestVersion: 1,
          createdAt: '2026-05-27T00:00:00Z',
          updatedAt: null,
          publishedAt: '2026-05-27T00:00:00Z',
          rootElement: {
            id: 'root',
            type: 'Stack',
            properties: {},
            children: [
              {
                id: 'fn',
                type: 'Text Input',
                properties: { label: 'First Name', bindTo: 'firstName' },
              },
              {
                id: 'age',
                type: 'Number Input',
                properties: { label: 'Age', bindTo: 'age' },
              },
              {
                id: 'active',
                type: 'Checkbox',
                properties: { label: 'Active', bindTo: 'isActive' },
              },
            ],
          },
        }),
      }),
    )
    await page.goto('/data/test-designer/new')
    // Wait for the form to settle — DynamicComponent renders a Skeleton
    // while the schema query is pending; once a labelled input exists, the
    // real form is in the DOM.
    await page
      .getByLabel(/first name/i)
      .first()
      .waitFor({ state: 'attached', timeout: 5_000 })
    assertNoCritical(await runAxe(page))
  })
})
