// Story 8.1 AC3 / AC9 — automated WCAG contrast gate for the theme token system.
//
// axe-core's `color-contrast` rule is a no-op under jsdom (it needs a real
// layout/paint to read computed colors), so it cannot guard the token *values*.
// Instead we re-derive the contrast ratios directly from the OKLCh values
// committed in `themes.css`: OKLCh → OKLab → linear sRGB → relative luminance →
// WCAG 2.x contrast. This makes the spec's §7.1 / §7.2 measured ratios an
// executable contract — reverting any WCAG value fix (AC3) or regressing a token
// fails this test, in every theme, with no browser required.
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { describe, expect, it } from 'vitest'

const themesCssPath = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../themes.css',
)
const css = readFileSync(themesCssPath, 'utf8')

type ThemeId = 'nord' | 'catppuccin' | 'gruvbox'
const THEMES: ThemeId[] = ['nord', 'catppuccin', 'gruvbox']

// Extract the `--token: value;` declarations inside a given [data-theme='id'] block.
function parseThemeBlock(themeId: ThemeId): Record<string, string> {
  const start = css.indexOf(`[data-theme='${themeId}']`)
  if (start === -1) throw new Error(`theme block not found: ${themeId}`)
  const open = css.indexOf('{', start)
  // Find the matching close brace (blocks are flat — no nested braces here).
  const close = css.indexOf('}', open)
  const body = css.slice(open + 1, close)
  const out: Record<string, string> = {}
  for (const line of body.split('\n')) {
    const m = line.match(/^\s*(--[\w-]+):\s*([^;]+);/)
    if (m) out[m[1]] = m[2].trim()
  }
  return out
}

const TOKENS: Record<ThemeId, Record<string, string>> = {
  nord: parseThemeBlock('nord'),
  catppuccin: parseThemeBlock('catppuccin'),
  gruvbox: parseThemeBlock('gruvbox'),
}

// --- color math -----------------------------------------------------------

// Parse `oklch(L C H)` or `oklch(L C H / a%)`. Returns null for non-oklch
// values (e.g. `var(--background)` aliases), which the callers skip.
function parseOklch(value: string): { L: number; C: number; H: number } | null {
  const m = value.match(
    /oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*(?:\/\s*[\d.]+%?)?\s*\)/,
  )
  if (!m) return null
  return { L: parseFloat(m[1]), C: parseFloat(m[2]), H: parseFloat(m[3]) }
}

// OKLCh → linear sRGB (Björn Ottosson's OKLab matrices).
function oklchToLinearSrgb({ L, C, H }: { L: number; C: number; H: number }) {
  const hr = (H * Math.PI) / 180
  const a = C * Math.cos(hr)
  const b = C * Math.sin(hr)
  const l_ = L + 0.3963377774 * a + 0.2158037573 * b
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b
  const s_ = L - 0.0894841775 * a - 1.291485548 * b
  const l = l_ ** 3
  const m = m_ ** 3
  const s = s_ ** 3
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ].map((v) => Math.min(1, Math.max(0, v)))
}

// WCAG relative luminance from linear-light RGB.
function relativeLuminance(value: string): number {
  const oklch = parseOklch(value)
  if (!oklch) throw new Error(`not an oklch value: ${value}`)
  const [r, g, b] = oklchToLinearSrgb(oklch)
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrast(fg: string, bg: string): number {
  const l1 = relativeLuminance(fg)
  const l2 = relativeLuminance(bg)
  const [hi, lo] = l1 >= l2 ? [l1, l2] : [l2, l1]
  return (hi + 0.05) / (lo + 0.05)
}

function ratioOf(theme: ThemeId, fgToken: string, bgToken: string): number {
  const t = TOKENS[theme]
  return contrast(t[fgToken], t[bgToken])
}

// --- AC3 / §7.1: text & component pairs must meet 4.5:1 -------------------

describe('theme token contrast — §7.1 text/component pairs (≥4.5:1)', () => {
  for (const theme of THEMES) {
    it(`${theme}: primary-foreground on primary ≥ 4.5`, () => {
      expect(ratioOf(theme, '--primary-foreground', '--primary')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: accent-foreground on accent ≥ 4.5`, () => {
      expect(ratioOf(theme, '--accent-foreground', '--accent')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: secondary-foreground on secondary ≥ 4.5`, () => {
      expect(ratioOf(theme, '--secondary-foreground', '--secondary')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: foreground on background ≥ 4.5`, () => {
      expect(ratioOf(theme, '--foreground', '--background')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: muted-foreground on background ≥ 4.5`, () => {
      expect(ratioOf(theme, '--muted-foreground', '--background')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: card-foreground on card ≥ 4.5`, () => {
      expect(ratioOf(theme, '--card-foreground', '--card')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: destructive-foreground on destructive ≥ 4.5`, () => {
      expect(ratioOf(theme, '--destructive-foreground', '--destructive')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: field-foreground (=foreground) on field ≥ 4.5`, () => {
      // --field-foreground is the shared alias var(--foreground); use --foreground here.
      expect(ratioOf(theme, '--foreground', '--field')).toBeGreaterThanOrEqual(4.5)
    })
    it(`${theme}: placeholder (=muted-foreground) on field ≥ 4.5`, () => {
      expect(ratioOf(theme, '--muted-foreground', '--field')).toBeGreaterThanOrEqual(4.5)
    })
  }
})

// --- §7.2: non-text UI boundaries must meet 3:1 ---------------------------

describe('theme token contrast — §7.2 non-text UI (≥3:1)', () => {
  for (const theme of THEMES) {
    it(`${theme}: focus ring on background ≥ 3.0`, () => {
      expect(ratioOf(theme, '--ring', '--background')).toBeGreaterThanOrEqual(3.0)
    })
    it(`${theme}: field-border on field ≥ 3.0`, () => {
      expect(ratioOf(theme, '--field-border', '--field')).toBeGreaterThanOrEqual(3.0)
    })
  }
})
