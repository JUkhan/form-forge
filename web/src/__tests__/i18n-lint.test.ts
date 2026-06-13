/// <reference types="node" />
// This test uses Node APIs (child_process, fs, os, path, __dirname). The app
// tsconfig only loads vite/client types; we opt this single file into Node's
// type definitions via a triple-slash reference rather than polluting the
// global types list for production code.
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup } from '@testing-library/react'
import { spawnSync } from 'node:child_process'
import {
  mkdtempSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

afterEach(cleanup)

const webRoot = resolve(__dirname, '..', '..')

describe('i18n lint script (AC-4)', () => {
  it('exits 0 against the real codebase — no missing en.json entries', () => {
    const result = spawnSync('node', ['scripts/i18n-check.mjs'], {
      cwd: webRoot,
      encoding: 'utf-8',
    })
    if (result.status !== 0) {
      throw new Error(`i18n-check failed:\n${result.stdout}\n${result.stderr}`)
    }
    expect(result.status).toBe(0)
  })

  it('reports a missing key as exit 1 in a synthetic fixture', () => {
    // Stage a minimal web/ replica in tmp: scripts/, src/lib/i18n/locales/, src/Page.tsx
    const tmp = mkdtempSync(join(tmpdir(), 'i18n-lint-fixture-'))
    try {
      const scriptsDir = join(tmp, 'scripts')
      const localesDir = join(tmp, 'src/lib/i18n/locales')
      const srcDir = join(tmp, 'src')
      mkdirSync(scriptsDir, { recursive: true })
      mkdirSync(localesDir, { recursive: true })

      // Copy the real script verbatim so we test what ships, not a fork.
      const realScript = readFileSync(
        join(webRoot, 'scripts/i18n-check.mjs'),
        'utf-8',
      )
      writeFileSync(join(scriptsDir, 'i18n-check.mjs'), realScript)

      // en.json has only the key 'present'; source uses both 'present' and the
      // missing 'absent' — the script must report 'absent' and exit 1.
      writeFileSync(
        join(localesDir, 'en.json'),
        JSON.stringify({ present: 'value' }),
      )
      writeFileSync(
        join(srcDir, 'Page.tsx'),
        "const a = t('present'); const b = t('absent');",
      )

      // glob resolves relative to the script's package; symlink node_modules so
      // the fixture script can import glob/ from the real install.
      const realNodeModules = join(webRoot, 'node_modules')
      symlinkSync(
        realNodeModules,
        join(tmp, 'node_modules'),
        'junction',
      )

      const result = spawnSync('node', ['scripts/i18n-check.mjs'], {
        cwd: tmp,
        encoding: 'utf-8',
      })
      expect(result.status).toBe(1)
      expect(result.stderr + result.stdout).toMatch(/absent/)
    } finally {
      rmSync(tmp, { recursive: true, force: true })
    }
  })
})

describe('i18n locale directory (AC-6)', () => {
  it('contains only en.json', () => {
    const localesDir = resolve(webRoot, 'src/lib/i18n/locales')
    const files = readdirSync(localesDir).filter((f) => !f.startsWith('.'))
    expect(files).toEqual(['en.json'])
  })
})

describe('i18n synchronous initialization (AC-5)', () => {
  it('i18n is initialized synchronously after config import', async () => {
    const { default: i18n } = await import('../lib/i18n/config')
    // initAsync: false guarantees isInitialized is true before any await.
    expect(i18n.isInitialized).toBe(true)
  })
})
