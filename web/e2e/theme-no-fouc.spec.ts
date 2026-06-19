import { test, expect } from '@playwright/test'

// Mock auth so the page can hydrate without a real backend.
async function mockAuth(page: import('@playwright/test').Page, themePreference: string | null = null) {
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
          themePreference,
          roles: [],
        },
      }),
    }),
  )
  await page.route('/api/auth/permissions', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({}),
    }),
  )
  await page.route('/api/menus/nav', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([]),
    }),
  )
}

test.describe('Theme: no flash of default-light (FOUC prevention)', () => {
  test('inline script applies stored theme before React nav is visible', async ({ page }) => {
    // Set ff-theme in localStorage before the page loads. The inline <script> in
    // <head> reads this and sets data-theme synchronously — before React hydrates.
    await page.addInitScript(() => {
      localStorage.setItem('ff-theme', 'slate-dark')
    })

    await mockAuth(page)

    // The inline script fires BEFORE domcontentloaded, so data-theme should already
    // reflect 'slate-dark' once the DOM is ready (asserted right after navigation).
    await page.goto('/', { waitUntil: 'domcontentloaded' })

    // Verify data-theme is 'slate-dark' immediately at DOMContentLoaded
    // (i.e., before any React rendering has occurred)
    const dataTheme = await page.evaluate(
      () => document.documentElement.getAttribute('data-theme'),
    )
    expect(dataTheme).toBe('slate-dark')
  })

  test('default-light applies when no theme is stored in localStorage', async ({ page }) => {
    // No localStorage value set — inline script should leave data-theme as default-light
    // (the static fallback set on <html> in index.html)
    await mockAuth(page)
    await page.goto('/', { waitUntil: 'domcontentloaded' })

    const dataTheme = await page.evaluate(
      () => document.documentElement.getAttribute('data-theme'),
    )
    expect(dataTheme).toBe('default-light')
  })

  test('invalid localStorage value falls back to default-light', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('ff-theme', 'hacker-green')
    })

    await mockAuth(page)
    await page.goto('/', { waitUntil: 'domcontentloaded' })

    const dataTheme = await page.evaluate(
      () => document.documentElement.getAttribute('data-theme'),
    )
    expect(dataTheme).toBe('default-light')
  })
})
