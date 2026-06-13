// One-off: capture Dataset Manager screenshots for the user guide.
// Drives the running dev app (http://localhost:5173) with Playwright's
// bundled Chromium, logging in through the UI as the seeded admin.
//
//   ADMIN_EMAIL=admin@formforge.local ADMIN_PASSWORD=... \
//     node web/scripts/capture-dataset-screenshots.mjs
//
// Output: docs/screenshots/14-admin-datasets.png, 15-create-dataset-dialog.png,
//         16-query-builder.png — 1440x900 @1x to match the existing set.
// The app must already be running, and the admin's theme should be Default
// Light so the shots match the rest of the set.

import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { chromium } from '@playwright/test'

const BASE = process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173'
const EMAIL = process.env.ADMIN_EMAIL ?? 'admin@formforge.local'
const PASSWORD = process.env.ADMIN_PASSWORD
if (!PASSWORD) throw new Error('Set ADMIN_PASSWORD (and optionally ADMIN_EMAIL) in the environment.')

const repoRoot = dirname(dirname(dirname(fileURLToPath(import.meta.url)))) // web/scripts -> web -> root
const outDir = join(repoRoot, 'docs', 'screenshots')

const browser = await chromium.launch()
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 })
const page = await ctx.newPage()

async function shot(name) {
  await page.screenshot({ path: join(outDir, name) })
  console.log('wrote', name)
}

// --- Log in ------------------------------------------------------------------
await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' })
await page.fill('#email', EMAIL)
await page.fill('#password', PASSWORD)
await page.click('button[type="submit"]')
await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 20_000 })

// --- 14. Dataset list --------------------------------------------------------
await page.goto(`${BASE}/admin/datasets`, { waitUntil: 'networkidle' })
await page.getByText('test2', { exact: true }).first().waitFor({ timeout: 15_000 })
await page.waitForTimeout(600)
await shot('14-admin-datasets.png')

// --- 15. Create Dataset form -------------------------------------------------
await page.getByRole('button', { name: /New Dataset/i }).first().click()
await page.getByText(/Create Dataset/i).first().waitFor({ timeout: 10_000 })
await page.waitForTimeout(400)
await shot('15-create-dataset-dialog.png')

// Dismiss the create form before navigating on.
await page.getByRole('button', { name: /^Cancel$/i }).first().click().catch(() => {})
await page.waitForTimeout(300)

// --- 16. Visual Query Builder (the "test2" Query Builder dataset) ------------
await page.getByRole('button', { name: /Open Builder/i }).first().click()
await page.waitForURL(/\/admin\/datasets\/[0-9a-f-]+/i, { timeout: 15_000 })
// Let the ReactFlow canvas + table nodes settle.
await page.waitForTimeout(2000)
await shot('16-query-builder.png')

await browser.close()
console.log('done')
