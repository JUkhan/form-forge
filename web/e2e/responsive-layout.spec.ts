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
    // At >=768 px the hamburger has md:hidden class -> display:none.
    const hamburger = page.getByRole('button', { name: /open menu/i })
    await expect(hamburger).not.toBeVisible()

    // The <nav> gets md:block -> always visible at desktop widths.
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
  // We skip explicitly at wider widths so the report shows "skipped" rather
  // than a vacuous "passed" with zero assertions.

  test('320 px — tapping nav item closes the drawer', async ({ page }) => {
    await mockAuth(page)
    await page.goto('/')

    const vp = page.viewportSize()
    test.skip(!vp || vp.width >= 768, 'Mobile-only test — no drawer at desktop widths')

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
