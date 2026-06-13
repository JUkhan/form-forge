# Story 7.6: Externalized String Architecture

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer,
I want to find all user-facing strings in resource files,
so that translation to additional languages is a configuration task, not a code change.

## Acceptance Criteria

**AC-1 — Zero hardcoded strings in TSX/TS files**
**Given** any TSX file in `web/src/`
**When** I grep for user-facing English strings in JSX
**Then** zero hardcoded strings appear; every user-facing string uses `t('key', ...)` per AR-33

**AC-2 — Every t() call has a corresponding en.json entry**
**Given** the `web/src/lib/i18n/locales/en.json` resource file
**When** I inspect it
**Then** every static `t('key')` literal call in the codebase has a corresponding entry

**AC-3 — API errors carry both messageKey and English detail**
**Given** any API error response
**When** I inspect the `ProblemDetails` payload
**Then** it contains both a `messageKey` extension (i18n key per AR-18) and an English `detail` string (per FR-49 AC-3)

**AC-4 — Automated orphan detection script**
**Given** an automated string-extractor or lint pass
**When** it runs against `web/src/`
**Then** orphaned `t('key')` calls (no en.json entry) and orphaned en.json entries (no `t()` callsite) are reported; missing entries gate CI

**AC-5 — Synchronous i18n initialization**
**Given** i18next is initialized
**When** the SPA boots
**Then** initialization is synchronous and completes before React renders (AR-33) — currently satisfied by `initAsync: false` in `web/src/lib/i18n/config.ts`

**AC-6 — Single locale file; architecture ready for additional locales**
**Given** v1 scope
**When** I inspect the locales directory
**Then** only `en.json` is present; adding a new locale requires only a new JSON file and `resources` entry in `config.ts`

---

## Tasks / Subtasks

- [x] **Task 1 — Create i18n orphan-detection script** (AC-4)
  - [x] Create `web/scripts/i18n-check.mjs` — see Dev Notes §1 for exact design
  - [x] Script must: (a) flatten `en.json` to a set of dot-notation keys, (b) scan `web/src/**/*.{ts,tsx}` excluding `__tests__/`, `*.test.*`, `*.spec.*`, (c) extract static `t('key')` and `t("key")` literals via regex, (d) report two lists: **missing** (t-call key absent from en.json) and **orphaned** (en.json key absent from all t() calls), (e) exit 1 if any **missing** entries found, exit 0 if only orphaned entries are found (orphans are warnings, not errors)
  - [x] Add `"lint:i18n": "node scripts/i18n-check.mjs"` to `web/package.json` scripts
  - [x] Add `lint:i18n` as a step in `.github/workflows/ci.yml` inside the `test-frontend` job (run after `npm run lint`)

- [x] **Task 2 — Audit and fix frontend hardcoded strings** (AC-1, AC-2)
  - [x] Run `npm run lint:i18n` (after Task 1) and fix every **missing** entry reported (script reported zero missing keys against the codebase before and after the audit)
  - [x] Scan `web/src/routes/` and `web/src/components/` for JSX string literals not wrapped in `t()`: patterns like `placeholder="English"`, `aria-label="English"`, `title="English"`, `>{English text}<` — see Dev Notes §2 for the grep pattern to use
  - [x] Add any missing en.json keys following the existing dot-notation convention (`admin.xxx`, `data.xxx`, etc.)
  - [x] Do NOT create new top-level keys unless none of the 10 existing top-level sections fit

- [x] **Task 3 — Audit and fix backend ProblemDetails for AC-3** (AC-3)
  - [x] Search `src/FormForge.Api/Features/**/*.cs` for all `Results.Problem(` calls
  - [x] For each call, verify BOTH: (1) `extensions["messageKey"]` is set, (2) the `detail` parameter (not in `extensions`) is set to an English string
  - [x] The `detail` parameter is the 3rd positional param or named `detail:` in `Results.Problem(detail: "...", statusCode: ..., extensions: ...)`
  - [x] Fix any `Results.Problem(` calls that have `messageKey` but are missing a `detail` string — infer the English detail from the `title` or from existing error semantics
  - [x] Fix any `Results.Problem(` calls that have neither `messageKey` nor `detail` — add both; pick a `messageKey` from the closest existing en.json `errors.*` entry or add a new key
  - [x] Do NOT change any existing `messageKey` values — only add missing ones

- [x] **Task 4 — Verify and document AC-5 and AC-6** (AC-5, AC-6)
  - [x] Confirm `web/src/lib/i18n/config.ts` uses `initAsync: false` (not `initImmediate`) — this is the i18next v25+ API; `initImmediate` was the pre-v25 API
  - [x] Confirm `web/src/lib/i18n/locales/` contains ONLY `en.json` (run: `ls web/src/lib/i18n/locales/`)
  - [x] Confirm `config.ts` imports `en` directly and passes it to `resources: { en: { translation: en } }` — adding a new locale requires only a new import + resources entry (no structural change)
  - [x] No code changes expected for AC-5/AC-6 — these are already compliant; document in Dev Agent Record

- [x] **Task 5 — Tests** (AC-1 through AC-6)
  - [x] Create `web/src/__tests__/i18n-lint.test.ts` — see Dev Notes §3 for design
  - [x] Test 1: Run `node scripts/i18n-check.mjs` via `spawnSync`; assert exit code is 0 (no missing keys)
  - [x] Test 2: Stage a synthetic fixture with a missing key and verify the script exits 1 reporting the absent key
  - [x] Test 3: Verify `web/src/lib/i18n/locales/` contains only `en.json` (readdir assertion)
  - [x] Test 4: Import `config.ts` and confirm `i18n.isInitialized === true` synchronously after import (no `await`)
  - [x] Add `afterEach(cleanup)` — project does NOT auto-register Vitest cleanup (confirmed in Story 7.3, 7.4, 7.5)
  - [x] Confirm all 164 existing frontend tests still pass; new tests bring total to 173 / 2 pre-existing failures unchanged

---

## Dev Notes

### §1 — i18n-check.mjs Script Design

Create `web/scripts/i18n-check.mjs`:

```javascript
import { readFileSync, readdirSync } from 'node:fs'
import { resolve, join } from 'node:path'
import { glob } from 'glob'  // glob is already a transitive dep via vite/vitest

const projectRoot = resolve(import.meta.dirname, '..')
const localesDir = join(projectRoot, 'src/lib/i18n/locales')
const srcDir = join(projectRoot, 'src')

// 1. Flatten en.json keys to a Set of dot-notation strings
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

// 2. Extract static t() key literals from source files
// Matches: t('foo.bar'), t("foo.bar"), t('foo.bar', ...), t("foo.bar", ...)
const T_LITERAL = /\bt\(\s*['"]([^'"]+)['"]/g

const files = await glob('**/*.{ts,tsx}', {
  cwd: srcDir,
  ignore: ['**/__tests__/**', '**/*.test.*', '**/*.spec.*'],
  absolute: true,
})

const codeKeys = new Set()
for (const file of files) {
  const src = readFileSync(file, 'utf-8')
  for (const match of src.matchAll(T_LITERAL)) {
    codeKeys.add(match[1])
  }
}

// 3. Compute missing (in code, not in JSON) and orphaned (in JSON, not in code)
const missing = [...codeKeys].filter(k => !jsonKeys.has(k))
const orphaned = [...jsonKeys].filter(k => !codeKeys.has(k))

if (missing.length) {
  console.error(`\n❌ Missing en.json entries (${missing.length}):`)
  for (const k of missing) console.error(`  - ${k}`)
}
if (orphaned.length) {
  console.warn(`\n⚠️  Orphaned en.json entries (${orphaned.length}):`)
  for (const k of orphaned) console.warn(`  - ${k}`)
}
if (!missing.length && !orphaned.length) {
  console.log('✅ i18n keys are clean — no missing or orphaned entries.')
}

process.exit(missing.length ? 1 : 0)
```

**Key design decisions:**
- Uses `glob` from the existing dep tree (available via vitest/vite) — do NOT add a new npm dep for this script
- Excludes test files (`__tests__/`, `*.test.*`, `*.spec.*`) because tests use identity-translator mocks that contain `t('key')` strings that are not real callsites
- Dynamic keys like `t(err.messageKey)` are NOT matched by the regex (no literal string) — this is correct; they are intentionally excluded
- Exits 1 only for **missing** entries (breaks CI); orphaned entries are warnings only (they don't break anything)
- The `import.meta.dirname` requires Node ≥ 20.11 or `--experimental-vm-modules` — confirm Node version or use `fileURLToPath(new URL('.', import.meta.url))` as fallback

**If `glob` is not available as a standalone import** (check `node_modules/.bin/glob`), use a recursive `readdirSync` fallback:
```javascript
import { readdirSync, statSync } from 'node:fs'
function walkTs(dir, result = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!['__tests__', 'node_modules'].includes(entry.name)) walkTs(join(dir, entry.name), result)
    } else if (/\.(ts|tsx)$/.test(entry.name) && !/(\.test\.|\.spec\.)/.test(entry.name)) {
      result.push(join(dir, entry.name))
    }
  }
  return result
}
```

### §2 — Hardcoded String Grep Pattern

Before Task 2, run this scan from `web/` to find potential hardcoded strings:

```bash
# Find JSX string content not wrapped in t()
grep -rn --include="*.tsx" --include="*.ts" \
  -E '(placeholder|title|label|aria-label|aria-describedby|aria-placeholder)="[A-Z][a-z]' \
  src/ --exclude-dir=__tests__ --exclude="*.test.*"

# Find JSX text content (text between tags)
grep -rn --include="*.tsx" -E '>\s*[A-Z][a-zA-Z ]{4,}\s*<' \
  src/ --exclude-dir=__tests__
```

**Known acceptable hardcoded strings** (do NOT replace these):
- `lang="en"` on `<html>` — not user-facing, HTML spec attribute
- `data-theme="default-light"` — technical attribute, not user text
- `__CSP_NONCE__` — placeholder string in `index.html`, not JSX
- Test files: identity-translator patterns like `t: (key) => key` — excluded
- `correlationId` values, UUIDs, ISO dates — not English prose
- The `'›'` separator in AdminBreadcrumb — a symbol, not a translated string

### §3 — Test Design for `i18n-lint.test.ts`

Create `web/src/__tests__/i18n-lint.test.ts`:

```typescript
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup } from '@testing-library/react'
import { execSync, spawnSync } from 'node:child_process'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

afterEach(cleanup)

const webRoot = resolve(__dirname, '../../..')

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
})

describe('i18n locale directory (AC-6)', () => {
  it('contains only en.json', () => {
    const localesDir = resolve(webRoot, 'src/lib/i18n/locales')
    const files = readdirSync(localesDir)
    expect(files).toEqual(['en.json'])
  })
})

describe('i18n synchronous initialization (AC-5)', () => {
  it('i18n is initialized synchronously after config import', async () => {
    const { default: i18n } = await import('../lib/i18n/config')
    // initAsync: false guarantees isInitialized is true before any await
    expect(i18n.isInitialized).toBe(true)
  })
})
```

**Note on `spawnSync` vs `execSync`:** Use `spawnSync` — it returns `status` directly without throwing on exit 1, making it easier to capture and report the failure message.

**Note on import test:** The `config.ts` dynamic import may be affected by Vitest module isolation. If the test fails due to module cache, add `{ resetModules: true }` to the Vitest config for this file, or restructure as an integration test using `spawnSync('node', ['-e', "import('./src/lib/i18n/config.js')..."])`.

### §4 — Backend ProblemDetails Audit (AC-3)

**Files to audit** (all `Results.Problem(` callsites):
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api/Features/Audit/AuditEndpoints.cs`
- `src/FormForge.Api/Features/Designer/DesignerAdminEndpoints.cs`
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`
- `src/FormForge.Api/Features/DynamicCrud/DynamicCrudEndpoints.cs`
- `src/FormForge.Api/Features/Menus/MenuEndpoints.cs`
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`

**AC-3 compliant pattern** (both `detail` and `messageKey` present):
```csharp
Results.Problem(
    detail: "Invalid email or password.",        // ← English detail (AC-3)
    statusCode: StatusCodes.Status401Unauthorized,
    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["messageKey"] = "auth.invalidCredentials",  // ← i18n key (AC-3)
        ["correlationId"] = httpContext.GetCorrelationId(),
    })
```

**Non-compliant patterns to fix:**
1. Has `extensions["messageKey"]` but **missing `detail:`** → add `detail: "..."` using the English text from the existing `messageKey`'s en.json value as the detail string
2. Has `detail:` but **missing `extensions["messageKey"]`** → add `["messageKey"] = "errors.genericError"` (or more specific key) to extensions
3. Has neither → add both

**The `title:` parameter is NOT the same as `detail:`** — `title` is a short RFC 7807 problem type name; `detail` is the human-readable explanation. Both can be set simultaneously.

**Do NOT change** `httpContext.GetCorrelationId()` patterns or existing `["code"]` extension values — only add missing `detail` or `messageKey` where absent.

### §5 — CI Integration (AC-4)

Add to `.github/workflows/ci.yml` in the `test-frontend` job, after the existing `npm run lint` step:

```yaml
- name: i18n key lint
  run: npm run lint:i18n
  working-directory: web
```

The `test-frontend` job already runs `npm install` before lint steps, so no additional setup is needed.

### §6 — File Locations

| Action | Path | Purpose |
|--------|------|---------|
| CREATE | `web/scripts/i18n-check.mjs` | Orphan detection script (AC-4) |
| MODIFY | `web/package.json` | Add `lint:i18n` script |
| MODIFY | `.github/workflows/ci.yml` | Add i18n lint step to test-frontend job |
| MODIFY | `web/src/lib/i18n/locales/en.json` | Add any missing keys found during audit |
| MODIFY | `src/FormForge.Api/Features/**/*.cs` | Add missing `detail` or `messageKey` to Results.Problem() calls |
| CREATE | `web/src/__tests__/i18n-lint.test.ts` | Tests for AC-4, AC-5, AC-6 |

### §7 — Critical Do-Nots

- **Do NOT rename or restructure en.json top-level keys** — 1,527 `t()` calls reference the current structure; renaming any key is a mass-change
- **Do NOT use `i18next-parser`** (npm package) as an alternative to the custom script — it installs a heavy dependency chain; the custom script uses only Node built-ins and the already-available `glob` dep
- **Do NOT mock `react-i18next` in `i18n-lint.test.ts`** — the test imports the real `config.ts` to verify synchronous init; mocking would defeat the purpose
- **Do NOT change `initAsync: false` to any other value** — this is the i18next v25+ API for synchronous initialization; changing it would break AR-33
- **Do NOT attempt to fix the 2 pre-existing `ReorderableMenuList.test.tsx` failures** — 164 pass / 2 fail is the current baseline
- **Do NOT add translations for non-user-facing strings** — console.log messages, TypeScript type names, CSS class names, and internal constants do not need to be translated

### §8 — Architecture Compliance

| AC | Architecture Reference | Implementation |
|----|----------------------|----------------|
| AC-1 | AR-33: all user-facing strings via `t()` | Audit and fix remaining hardcoded strings |
| AC-2 | AR-33: key naming convention, en.json completeness | Orphan detection script + fix missing entries |
| AC-3 | AR-18: messageKey in ProblemDetails; FR-49 AC-3: English detail | Audit all Results.Problem() calls for both fields |
| AC-4 | FR-49 AC-4: string extraction/lint | `web/scripts/i18n-check.mjs` + `lint:i18n` + CI step |
| AC-5 | AR-33: synchronous init; Decision 4.8 | Already compliant: `initAsync: false` in config.ts |
| AC-6 | FR-49 AC-4: config-only locale addition | Already compliant: single en.json, resources pattern in config.ts |

### §9 — Previous Story Learnings (from 7.4, 7.5)

- `afterEach(cleanup)` is REQUIRED in every new `*.test.tsx`/`*.test.ts` file — project does NOT auto-register Vitest cleanup
- The pre-existing failure baseline is **164 pass / 2 fail** (`ReorderableMenuList.test.tsx`); new tests must not change this
- This is an audit+tooling story. The dev agent should resist over-engineering the i18n-check script. The implementation in §1 is final — do not add complexity like config files, watchers, or caching
- `web/src/lib/i18n/config.ts` uses `initAsync: false` (i18next v26 API) — not `initImmediate` (pre-v25 API). The installed version is `i18next@^26.2.0`

### §10 — Git Intelligence (most recent work)

- `6ce4b6f` (Story 7.5 code review) — last commit. Added `sectionLabels` keys for `designers` and `data` to `en.json`; added `routeFileIgnorePattern` extension in `vite.config.ts`; no changes to `config.ts`, `locales/` structure, or backend endpoints
- The `initAsync: false` config has been in place since Story 7.2 (theme feature first used i18n) — no change needed
- CI file `.github/workflows/ci.yml` was established in Story 7.4; the `test-frontend` job is the correct location for the new lint step

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- `lint:i18n` initial run against current codebase: exit 0, 0 missing, 57 orphans (informational only — orphans are warnings, not gating).
- Final `lint:i18n` after frontend audit: exit 0, 0 missing, 149 orphans (orphan count grew with new helper keys added for fields/options/placeholders/help that are referenced by t() calls in the designer; the script counts them only against static literals, so dynamic `t(varKey)` lookups against backend `messageKey` strings appear as orphans by design).
- Frontend Vitest: 173 pass / 2 fail. The 2 failures are the pre-existing `ReorderableMenuList.test.tsx` `reorderMutation.mutate is not a function` errors (`164 baseline + 5 indirect new + 4 new i18n-lint = 173`).
- Backend `dotnet test`: 536 pass / 3 fail. Same 3 pre-existing failures noted in Story 7.3 commit message (audit-recovery + 405/404 verb-routing).
- Hardcoded-string grep audit after Task 2 is clean: only false positives remain (`Promise<void>` TypeScript syntax, test files which the lint script already excludes).

### Completion Notes List

- **Task 1 (AC-4):** Created `web/scripts/i18n-check.mjs` exactly per Dev Notes §1. `glob` already resolves transitively via the existing dep tree (verified with a probe `node -e "import('glob')..."` before writing the script — see Dev Notes §1's "If glob is not available" fallback, which proved unnecessary). The script uses `import.meta.dirname` (Node 22.15 in this env, well above the 20.11 requirement). Added `lint:i18n` script to `web/package.json` and a `- run: npm run lint:i18n` step to `.github/workflows/ci.yml` `test-frontend` job, immediately after `npm run lint` per Dev Notes §5.
- **Task 2 (AC-1, AC-2):** Static `t()` callsite audit was clean from the start (script reported 0 missing), so the work focused on the JSX-attribute / text-content audit from Dev Notes §2. The five files with hardcoded user-facing strings — `web/src/components/designer/PropertyInspector.tsx` (~120 strings), `web/src/components/designer/DesignerCanvas.tsx`, `web/src/components/designer/ElementRenderer.tsx`, `web/src/components/ui/dialog.tsx`, `web/src/routes/_app/designer.library.tsx`, `web/src/routes/_app/designer.$designerId.tsx` — were converted to `t()` calls. New keys were added under existing top-level sections per Dev Notes §7: `designer.canvas.*` was extended with `addTab`, `noTabsClickAddTab`, `dragFieldsToRowTemplate`, `tabularDisplayBanner`, `deleteConfirmPrompt`, `confirmDeleteAria`, `cancelDeleteAria`, `deleteElement`, `addPreview`, `tabFallbackName`, `componentDisplayNameAria`, `rowFormStatus`/`StatusNotSet`/`StatusVersionLatest`, `nodeBadgeTabs`/`Repeater`, `dropElementsHere`; a new `designer.inspector.fields|options|placeholders|help` namespace covers the ~80 Field labels, dropdown option strings, placeholder hints, and span help text in `PropertyInspector.tsx`; a new `designer.renderer.*` namespace covers the data-entry-runtime strings in `ElementRenderer.tsx` (`imageUnavailable`, `imageEmptyPlaceholder`, `noRowsYet`, `dropFieldsToTableHelp`, `configureRowTemplate`, `addRow`, `addPreview`, `actionsHeader`, `editRow`, `deleteRow`, `rowFormStatus`/`StatusNotSet`/`StatusVersionLatest`); `common.close` was added for the shadcn dialog primitive. `dialog.tsx` (a shadcn UI primitive) imported `useTranslation` — first shadcn primitive in `components/ui/` to do so; the alternative was a `closeLabel` prop, but inlining `t('common.close')` matches how the other primitives in this codebase get their strings (none had been i18n-wired before this story). `ElementRenderer.tsx` had no `useTranslation` import at all — added at file scope and added `const { t } = useTranslation()` to each component that needed it (`ImageRenderer`, `TabsRenderer`, `RepeaterPreview`, `RepeaterTablePreview`, `RepeaterInteractiveTable`, `RepeaterInteractive`). Tags inside translated strings (e.g. `<em>` for visibility help and `<code>` for `fieldName`) ride through via `dangerouslySetInnerHTML` since react-i18next's default Trans-component setup isn't wired here and the strings are author-trusted constants from en.json. Acceptable hardcoded strings from Dev Notes §2 (`lang="en"`, `data-theme="default-light"`, `__CSP_NONCE__`, the `›` separator in AdminBreadcrumb, test-file `t: (key) => key` identity translators) were left untouched as documented.
- **Task 3 (AC-3):** Audited every `Results.Problem(` callsite across `Features/Auth/AuthEndpoints.cs`, `Features/Audit/AuditEndpoints.cs`, `Features/Designer/DesignerEndpoints.cs`, `Features/Designer/DesignerAdminEndpoints.cs`, `Features/DynamicCrud/DynamicDataEndpoints.cs`, `Features/Menus/MenuAdminEndpoints.cs`, `Features/Users/UserEndpoints.cs`, `Features/Roles/RoleEndpoints.cs`, `Common/Endpoints/RouteGroupExtensions.cs`, `Common/Endpoints/EndpointFilters/ValidationFilter.cs`. Three classes of non-compliance found and fixed: (1) **had `messageKey` but missing `detail`** — added `detail:` populated with the English text from the matching en.json value (or, where the messageKey was a backend-only key without a frontend entry, with a sensible English string close to the existing `title:`); (2) **had `detail:` but missing `messageKey`** — none of these existed in practice (every detail-bearing call already had a messageKey); (3) **had neither — fallback `_ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)`** — 21 of these existed across the audited files. Each was rewritten to `Results.Problem(detail: "Something went wrong. Please try again.", statusCode: StatusCodes.Status500InternalServerError, extensions: { ["messageKey"] = "errors.genericError" })`. The `errors.genericError` key already exists in en.json. The `Results.ValidationProblem(...)` callsite in `ValidationFilter.cs` line ~40 was left untouched — `ValidationProblem` is a distinct API that emits the standard `errors` extensions per RFC 7807 §6.3.1 for FluentValidation rule failures; per Dev Notes §4 the audit scope is `Results.Problem(`, not `Results.ValidationProblem(`. Per Dev Notes §4 §7 no existing `messageKey` values were changed — only `detail:` and (for the 500 fallbacks) the missing `messageKey` were added. The `correlationId` extension was added only where the handler already had access to `HttpContext` (already-correlated calls in AuthEndpoints retained their existing `httpContext.GetCorrelationId()`); fallback 500s in handlers that don't already accept `HttpContext` were left without `correlationId` rather than churning every handler signature.
- **Task 4 (AC-5, AC-6):** No code changes required. Verified: `web/src/lib/i18n/config.ts` already passes `initAsync: false` (i18next v26 API per `package.json`); `web/src/lib/i18n/locales/` contains exactly one file, `en.json`; the `resources: { en: { translation: en } }` shape means adding a new locale is purely additive (one new JSON file + one new import + one new `resources` entry).
- **Task 5 (AC-1..AC-6):** Created `web/src/__tests__/i18n-lint.test.ts` with 4 tests covering AC-4 (real-codebase exit-0 + synthetic-fixture exit-1 with missing key), AC-5 (synchronous `i18n.isInitialized` after `config` import), and AC-6 (`readdirSync(locales)` returns exactly `['en.json']`). The synthetic-fixture test stages a minimal `web/`-shaped tree in `mkdtempSync` and symlinks the real `node_modules` so the fixture run of the script can resolve `glob` — that way the test exercises the actual shipped script byte-for-byte instead of a fork. `afterEach(cleanup)` registered per the project convention.

**AC verification:**
- **AC-1** ✓ — JSX-attribute and text-content grep audit returns zero hits beyond TypeScript `Promise<void>` false positives and excluded test files; all designer/UI/route hardcoded strings now use `t()`.
- **AC-2** ✓ — `npm run lint:i18n` exits 0 with zero **missing** entries against the real codebase; orphans are informational only per the script's spec.
- **AC-3** ✓ — every `Results.Problem(` callsite in the audited backend files now has both a `detail:` English string and an `extensions["messageKey"]` (script-grade audit via paren-balanced Python pass returned zero non-compliant calls).
- **AC-4** ✓ — `web/scripts/i18n-check.mjs` + `lint:i18n` npm script + CI step + 2 Vitest tests; runs in CI via the new `npm run lint:i18n` step in `test-frontend`.
- **AC-5** ✓ — `initAsync: false` is in `config.ts`; the new Vitest test asserts `i18n.isInitialized === true` synchronously after import.
- **AC-6** ✓ — only `en.json` in `locales/`; the new Vitest test asserts `readdirSync` returns `['en.json']`.

### File List

**Created:**
- `web/scripts/i18n-check.mjs`
- `web/src/__tests__/i18n-lint.test.ts`

**Modified (frontend):**
- `web/package.json` — added `lint:i18n` npm script.
- `.github/workflows/ci.yml` — added `npm run lint:i18n` step to `test-frontend` job.
- `web/src/lib/i18n/locales/en.json` — extended `common`, `designer.canvas`, `designer.inspector` (with new `fields`/`options`/`placeholders`/`help` subobjects), added new `designer.renderer.*` subtree.
- `web/src/components/ui/dialog.tsx` — replaced two hardcoded "Close" strings with `t('common.close')`; first shadcn primitive in `components/ui/` to use `useTranslation`.
- `web/src/components/designer/DesignerCanvas.tsx` — replaced ~12 hardcoded strings (NodeBadge labels, `+ Add Tab`, `No tabs — click…`, tabular banner, `Drag fields here…`, `+ Add (preview)`, row-form status, `Delete?`, delete aria-labels) and the two `Tab ${i+1}` template strings via the new `designer.canvas.*` keys.
- `web/src/components/designer/ElementRenderer.tsx` — added top-level `useTranslation` import and per-component `const { t } = useTranslation()` in six renderer components; replaced "Image", "Image unavailable", "No rows yet", "Drop one or more Repeater Field…", "Configure the row template…", "+ Add", "+ Add (preview)", row-form status strings, `aria-label="Actions/Edit row/Delete row"`, and the `Tab ${i+1}` fallback in `TabsRenderer`.
- `web/src/components/designer/PropertyInspector.tsx` — added `useTranslation` hook to every component that needed it; replaced ~120 hardcoded strings (Field labels, BoolField labels, SelectItem text, placeholders, help-text spans, the conditional-visibility header, the no-selection hint, the Tab section label).
- `web/src/routes/_app/designer.library.tsx` — `aria-label="Actions"` → `t('designer.renderer.actionsHeader')`.
- `web/src/routes/_app/designer.$designerId.tsx` — `aria-label="Component display name"` → `t('designer.canvas.componentDisplayNameAria')`.

**Modified (backend):**
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — added `detail:` to the three branded Problem helpers (InvalidCredentials, AccountInactive, RefreshTokenInvalidResponse); rewrote one fallback 500 to include `detail` + `messageKey` + correlationId.
- `src/FormForge.Api/Features/Audit/AuditEndpoints.cs` — added `detail:` to both Designer-not-found 404 branches (replace_all).
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` — added `detail:` to `FieldKeyValidationProblem`, `VersionConflictProblem`, `DesignerNotFoundProblem`, `DuplicateConflictProblem`, `DuplicateIdTooLongProblem`, `DesignerExistsProblem`, `VersionNotFoundProblem`, `VersionNotPublishedProblem`, `PublishConflictProblem`, `StatusInvalidProblem`; rewrote 4 fallback 500s.
- `src/FormForge.Api/Features/Designer/DesignerAdminEndpoints.cs` — added `detail:` to `ColumnProtected`, `ColumnNotOrphaned`, `DesignerNotFoundProblem`, `ColumnNotFoundProblem`; rewrote 1 fallback 500.
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` — added `detail:` to the six `Problems.*` helpers (`TableNotProvisioned`, `RecordNotFound`, `RecordDeleted`, `RecordAlreadyDeleted`, `RecordNotDeleted`, `ChildNotFound`); `ValidationFailed` already had detail.
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — added `detail:` to `ParentNotFound`, `MaxDepthExceeded`, `HasChildren`, `RolesNotFound`, `Conflict` (assign roles), `MenusNotFound`, `MixedScopes`, `Conflict` (reorder), `DesignerNotFound`, `VersionNotPublished`, `RepeaterCycle`, `NoBinding`, the inline `targetVersion <= 0` validation, `MenuNotFoundProblem`, `UploadInvalid`; rewrote 8 fallback 500s.
- `src/FormForge.Api/Features/Roles/RoleEndpoints.cs` — added `detail:` to `HasAssignments`, `RoleNotFoundProblem`, `RoleNameConflictProblem`, `RoleSystemProtectedProblem`; rewrote 3 fallback 500s.
- `src/FormForge.Api/Features/Users/UserEndpoints.cs` — added `detail:` to `RolesNotFound`, `LastAdminLockout`, `Conflict`, `UserNotFoundProblem`, `UserEmailConflictProblem`, `SelfDeactivationProblem`; rewrote 5 fallback 500s.
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` — added `detail:` + `messageKey` to the 400 (missing-designerId) and 403 (permission-denied) branches of `RequirePermission`.
- `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs` — added `detail:` + `messageKey` to the 400 (invalid-body) branch.

**Modified (planning artifacts):**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `7-6-externalized-string-architecture: ready-for-dev` → `in-progress` (Step 4) → `review` (Step 9).

### Review Findings

- [x] [Review][Decision] dangerouslySetInnerHTML without sanitization for PropertyInspector help texts — accepted as-is: en.json is developer-controlled source code, strings use only `<em>`/`<code>` tags, designer is an authenticated-admin-only surface. No action needed unless translation CMS is adopted.
- [x] [Review][Patch] `errors.badRequest` messageKey missing from en.json [`web/src/lib/i18n/locales/en.json` + `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`]
- [x] [Review][Patch] `errors.invalidRequestBody` messageKey missing from en.json [`web/src/lib/i18n/locales/en.json` + `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs`]
- [x] [Review][Patch] `require('node:fs')` used in ESM test file — `readFileSync` is already imported; `symlinkSync` should be added to top-level import instead of using `require()` [`web/src/__tests__/i18n-lint.test.ts:35,55`]
- [x] [Review][Patch] AC-6 test brittle with OS hidden files — `expect(files).toEqual(['en.json'])` will fail if OS creates `.DS_Store` or similar; filter dot-files before asserting [`web/src/__tests__/i18n-lint.test.ts:77`]
- [x] [Review][Defer] Pre-existing `errors.*` en.json gaps (validationFailed, tableNotProvisioned, notFound, recordDeleted, recordAlreadyDeleted, recordNotDeleted, childNotFound) — deferred, pre-existing
- [x] [Review][Defer] Pre-existing messageKey namespace mismatches in UserEndpoints/RoleEndpoints/MenuAdminEndpoints (`users.*`, `roles.*`, bare `menus.*` keys) — out of scope per spec "Do NOT change existing messageKey values", deferred, pre-existing
- [x] [Review][Defer] `dropFieldsToTableHelp` en.json value is plain text — prior JSX used `<code>Repeater Field</code>` visual emphasis; now plain text in en.json with no HTML tag — deferred, cosmetic regression

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-27 | Story 7.6 created — Externalized String Architecture | bmad-create-story |
| 2026-05-27 | Story 7.6 implemented — orphan-detection script + frontend hardcoded-string audit + backend ProblemDetails audit + i18n-lint tests | bmad-dev-story |
| 2026-05-27 | Story 7.6 code review — 1 decision-needed, 3 patches, 3 deferred, ~17 dismissed | bmad-code-review |
