#!/usr/bin/env node
// i18n orphan-detection script (Story 7.6 AC-4).
// Reports keys in en.json with no t() callsite (orphaned — warning),
// and t() callsites with no en.json entry (missing — exit 1).
import { readFileSync, readdirSync } from 'node:fs'
import { resolve, join } from 'node:path'

const projectRoot = resolve(import.meta.dirname, '..')
const localesDir = join(projectRoot, 'src/lib/i18n/locales')
const srcDir = join(projectRoot, 'src')

function flattenKeys(obj, prefix = '') {
  const keys = new Set()
  for (const [k, v] of Object.entries(obj)) {
    const full = prefix ? `${prefix}.${k}` : k
    if (v !== null && typeof v === 'object' && !Array.isArray(v)) {
      for (const sub of flattenKeys(v, full)) keys.add(sub)
    } else {
      keys.add(full)
    }
  }
  return keys
}

const enJson = JSON.parse(readFileSync(join(localesDir, 'en.json'), 'utf-8'))
const jsonKeys = flattenKeys(enJson)

// Matches static literals only: t('foo.bar'), t("foo.bar"), t('foo.bar', ...), t("foo.bar", ...).
// Dynamic args like t(err.messageKey) are intentionally excluded.
const T_LITERAL = /\bt\(\s*['"]([^'"]+)['"]/g

// Collect every .ts/.tsx under src/ via Node's built-in recursive readdir (no external
// `glob` dependency — it was undeclared and broke `npm ci` runs). Skip test files and
// __tests__ dirs, matching the previous ignore globs.
const files = readdirSync(srcDir, { recursive: true, withFileTypes: true })
  .filter((d) => d.isFile() && /\.(ts|tsx)$/.test(d.name))
  .map((d) => join(d.parentPath ?? d.path, d.name))
  .filter((f) => !/[\\/]__tests__[\\/]/.test(f) && !/\.(test|spec)\./.test(f))

const codeKeys = new Set()
for (const file of files) {
  const src = readFileSync(file, 'utf-8')
  for (const match of src.matchAll(T_LITERAL)) {
    codeKeys.add(match[1])
  }
}

const missing = [...codeKeys].filter((k) => !jsonKeys.has(k))
const orphaned = [...jsonKeys].filter((k) => !codeKeys.has(k))

if (missing.length) {
  console.error(`\n❌ Missing en.json entries (${missing.length}):`)
  for (const k of missing.sort()) console.error(`  - ${k}`)
}
if (orphaned.length) {
  console.warn(`\n⚠️  Orphaned en.json entries (${orphaned.length}):`)
  for (const k of orphaned.sort()) console.warn(`  - ${k}`)
}
if (!missing.length && !orphaned.length) {
  console.log('✅ i18n keys are clean — no missing or orphaned entries.')
}

process.exit(missing.length ? 1 : 0)
