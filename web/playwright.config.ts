import { defineConfig, devices } from '@playwright/test'

// Story 7.4 AC-6: e2e is a required CI check, so the test run must be
// self-contained — no manually-started dev server. `webServer` boots
// `vite preview` (serving the production build from web/dist) on a fixed
// port. CI runs `npm run build` immediately before `npx playwright test`;
// locally, a developer who already has preview running on 4173 gets
// `reuseExistingServer` so the suite doesn't fight them for the port.
//
// Note: `vite preview` does NOT build — dist must exist. CI sequences this
// in .github/workflows/ci.yml; for local first runs the developer needs
// `npm run build` first, or runs `npm run build && npm run preview` once.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'html',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:4173',
    trace: 'on-first-retry',
  },
  webServer: {
    command: 'npm run preview -- --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
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
