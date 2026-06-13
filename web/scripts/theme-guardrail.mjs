#!/usr/bin/env node
// Theme-token guardrail (Story 8.1 Task 7 / AC-7).
// Fails (exit 1) if any hardcoded color literal that bypasses the semantic
// token system reappears under web/src/components, /features, or /routes.
// Run via `npm run lint:theme`; kept OUT of the eslint config so it can't
// destabilise the existing lint run (Story 8.1 Task 7 constraint).
//
// Why a grep and not an eslint rule: the offenders are Tailwind class-string
// substrings inside cn()/className literals AND inline-style hex; a className
// linter (eslint-plugin-tailwindcss) wouldn't catch the inline-style hex and
// would add a heavy dependency. A literal scan covers both with zero deps
// beyond `glob` (already used by lint:i18n).
//
// Escape hatch: a line carrying the marker `theme-guardrail-ignore` is skipped
// — use it (with a justifying comment) for a genuinely theme-agnostic case.
import { readFileSync } from 'node:fs'
import { resolve, join, relative } from 'node:path'
import { glob } from 'glob'

const projectRoot = resolve(import.meta.dirname, '..')
const srcDir = join(projectRoot, 'src')

// Each entry: [regex, human label]. Mirrors the AC-7 verification search.
const FORBIDDEN = [
  [/\bdark:/, 'dark: variant (deleted in Story 8.1 — themes are data-theme only)'],
  [/\bbg-white\b/, 'bg-white → bg-card / bg-background'],
  [/\bbg-slate-\d/, 'bg-slate-* → bg-muted / bg-card'],
  [/\btext-slate-\d/, 'text-slate-* → text-foreground / text-muted-foreground'],
  [/\btext-white\b/, 'text-white → text-*-foreground'],
  [/\bborder-slate-\d/, 'border-slate-* → border-border / border-field-border'],
  [/\bdivide-slate-\d/, 'divide-slate-* → divide-border'],
  [/\bring-white\b/, 'ring-white → ring-background'],
  [/\b(bg|border|text)-red-\d/, 'red-* → destructive'],
  [/\b(bg|text|border)-amber-\d/, 'amber-* → destructive (caution) / muted (advisory)'],
  [/\b(bg|text)-gray-\d/, 'gray-* → muted / muted-foreground'],
  [/#(?:666|555|ddd)\b/, 'inline hex #666/#555/#ddd → var(--token)'],
]

const files = await glob('src/{components,features,routes}/**/*.{ts,tsx}', {
  cwd: projectRoot,
  ignore: ['**/__tests__/**', '**/*.test.{ts,tsx}', '**/*.spec.{ts,tsx}'],
})

const violations = []
for (const rel of files) {
  const lines = readFileSync(join(projectRoot, rel), 'utf-8').split('\n')
  lines.forEach((line, i) => {
    if (line.includes('theme-guardrail-ignore')) return
    for (const [re, label] of FORBIDDEN) {
      if (re.test(line)) {
        violations.push(`${relative(projectRoot, join(projectRoot, rel))}:${i + 1}  ${label}\n    ${line.trim()}`)
        break
      }
    }
  })
}

if (violations.length > 0) {
  console.error(`\n✖ theme-guardrail: ${violations.length} hardcoded-color violation(s):\n`)
  console.error(violations.join('\n'))
  console.error('\nReplace with a semantic token, or add `theme-guardrail-ignore` + a justifying comment.\n')
  process.exit(1)
}

console.log('✓ theme-guardrail: no hardcoded colors under components/features/routes')
