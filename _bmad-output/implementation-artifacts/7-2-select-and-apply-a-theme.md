# Story 7.2: Select and Apply a Theme

Status: done

## Story

As any user,
I want to select from three visual themes,
so that the interface suits my preference.

## Acceptance Criteria

**AC-1 — Theme selector shows three options**
**Given** I open the theme selector in the app header
**When** I view the options
**Then** three themes are listed: `default-light`, `slate-dark`, `solarized`

**AC-2 — Theme applies immediately without page reload**
**Given** I select a theme from the selector
**When** the selection commits
**Then** the theme applies immediately without page reload
**And** the underlying mechanism is Tailwind 4 CSS variables keyed off `[data-theme='...']` on `<html>` (per AR-27)

**AC-3 — Theme persists across client navigations**
**Given** the theme is applied
**When** I navigate between routes using TanStack Router
**Then** the theme persists (no flash, no reset)

---

## Tasks / Subtasks

- [x] **Task 1 — Create `web/src/lib/theme/themes.ts`** (AC-1, AC-2)
  - [x] Export `THEMES` as a const tuple: `['default-light', 'slate-dark', 'solarized'] as const`
  - [x] Export `type Theme = (typeof THEMES)[number]`
  - [x] Export `DEFAULT_THEME: Theme = 'default-light'`
  - [x] Export `THEME_LABELS` map (for i18n key lookup): see Dev Notes §2

- [x] **Task 2 — Create `web/src/lib/theme/applyTheme.ts`** (AC-2, AC-3)
  - [x] `applyTheme(theme: Theme): void` — sets `document.documentElement.setAttribute('data-theme', theme)` and `localStorage.setItem('ff-theme', theme)`
  - [x] `getStoredTheme(): Theme` — reads `localStorage.getItem('ff-theme')`, validates against `THEMES`, returns `DEFAULT_THEME` if invalid/absent
  - [x] Both functions must guard against SSR/non-browser environments: check `typeof window !== 'undefined'`

- [x] **Task 3 — Create `web/src/lib/theme/ThemeProvider.tsx`** (AC-2, AC-3)
  - [x] Create `ThemeContext` with shape `{ theme: Theme; setTheme: (t: Theme) => void }`
  - [x] `ThemeProvider` component: on mount calls `getStoredTheme()` → `applyTheme()` → sets state; exposes context
  - [x] `setTheme` calls `applyTheme(t)` then updates React state
  - [x] Export `useTheme()` hook that throws if used outside `ThemeProvider`
  - [x] See Dev Notes §3 for full implementation

- [x] **Task 4 — Create `web/src/styles/themes.css`** (AC-2)
  - [x] Define `[data-theme='default-light']` — current `:root` values (verbatim copy from `index.css:51-84`)
  - [x] Define `[data-theme='slate-dark']` — current `.dark` values (verbatim copy from `index.css:86-118`)
  - [x] Define `[data-theme='solarized']` — Solarized Light palette (see Dev Notes §4)
  - [x] See Dev Notes §4 for exact CSS

- [x] **Task 5 — Update `web/src/index.css`** (AC-2)
  - [x] Add `@import './styles/themes.css';` after the existing `@import` block (before `@custom-variant`)
  - [x] Replace `@custom-variant dark (&:is(.dark *));` with `@custom-variant dark (&:is([data-theme='slate-dark'] *));`
  - [x] **Delete** the entire `:root { ... }` block (lines 51–84 — these move to `themes.css` as `[data-theme='default-light']`)
  - [x] **Delete** the entire `.dark { ... }` block (lines 86–118 — these move to `themes.css` as `[data-theme='slate-dark']`)
  - [x] Keep `@theme inline { ... }` block unchanged
  - [x] Keep `@layer base { ... }` block unchanged
  - [x] Keep all four `@import "..."` lines at the top unchanged

- [x] **Task 6 — Set default `data-theme` on `<html>` in `web/index.html`** (AC-2)
  - [x] Change `<html lang="en">` to `<html lang="en" data-theme="default-light">`
  - [x] Do NOT add an inline `<script>` — that is Story 7.3's responsibility (no-flash for page reloads with server-persisted preference)
  - [x] The `data-theme` attribute ensures CSS variables are resolved immediately on page load before React mounts

- [x] **Task 7 — Wire `ThemeProvider` in `web/src/routes/__root.tsx`** (AC-3)
  - [x] Import `ThemeProvider` from `../lib/theme/ThemeProvider`
  - [x] Wrap `<Outlet />` inside `<ThemeProvider>` so the context is available in all routes (including login)
  - [x] See Dev Notes §5 for updated `__root.tsx`

- [x] **Task 8 — Add theme selector in `web/src/routes/_app.tsx`** (AC-1, AC-2)
  - [x] Import `{ useTheme }` from `../lib/theme/ThemeProvider`
  - [x] Import `{ THEMES }` from `../lib/theme/themes`
  - [x] Import `{ Select, SelectContent, SelectItem, SelectTrigger, SelectValue }` from `../components/ui/select`
  - [x] Add the `<Select>` theme picker to the `<header>` in `AppLayout` (before the logout button)
  - [x] Use `t('theme.label')` as accessible label; display `t('theme.{key}')` per option
  - [x] `onValueChange` calls `setTheme(value as Theme)`
  - [x] See Dev Notes §6 for full JSX

- [x] **Task 9 — Add i18n keys to `web/src/lib/i18n/locales/en.json`** (AC-1)
  - [x] Add a `"theme"` section — see Dev Notes §7 for exact keys

- [x] **Task 10 — Write tests** (unit coverage for the new theme infrastructure)
  - [x] Create `web/src/lib/theme/__tests__/theme.test.ts` covering `applyTheme`, `getStoredTheme`
  - [x] Create `web/src/lib/theme/__tests__/ThemeProvider.test.tsx` covering context provision and `setTheme`
  - [x] See Dev Notes §8 for test outlines

---

## Dev Notes

### §1 — Scope Boundaries

**In scope:**
- Three CSS themes (`default-light`, `slate-dark`, `solarized`) via `[data-theme='...']` CSS variables
- `ThemeProvider` + `applyTheme` infrastructure (localStorage only — no server API)
- Theme selector UI in the `_app.tsx` header using shadcn `<Select>` (already installed)
- Default `data-theme="default-light"` on `<html>` prevents unstyled flash before React mounts
- Client-navigation persistence via React context (AC-3)

**NOT in scope (Story 7.3):**
- Server persistence via `PUT /api/users/me/preferences { themePreference }`
- Inline `<script nonce>` in `<head>` for page-reload no-flash (that requires CSP nonce injection from `IndexHtmlRewriter.cs`)
- Syncing server-stored preference to localStorage after login

**NOT in scope (Story 7.4):**
- Contrast ratio compliance ≥4.5:1 audit across themes
- axe-core gate

### §2 — `themes.ts`

```typescript
// web/src/lib/theme/themes.ts
export const THEMES = ['default-light', 'slate-dark', 'solarized'] as const
export type Theme = (typeof THEMES)[number]
export const DEFAULT_THEME: Theme = 'default-light'

export const THEME_LABELS: Record<Theme, string> = {
  'default-light': 'theme.defaultLight',
  'slate-dark': 'theme.slateDark',
  solarized: 'theme.solarized',
}
```

### §3 — `applyTheme.ts` and `ThemeProvider.tsx`

```typescript
// web/src/lib/theme/applyTheme.ts
import { THEMES, DEFAULT_THEME, type Theme } from './themes'

export function applyTheme(theme: Theme): void {
  if (typeof window === 'undefined') return
  document.documentElement.setAttribute('data-theme', theme)
  localStorage.setItem('ff-theme', theme)
}

export function getStoredTheme(): Theme {
  if (typeof window === 'undefined') return DEFAULT_THEME
  const stored = localStorage.getItem('ff-theme')
  return (THEMES as readonly string[]).includes(stored ?? '') ? (stored as Theme) : DEFAULT_THEME
}
```

```tsx
// web/src/lib/theme/ThemeProvider.tsx
import { createContext, useContext, useState, useEffect, type ReactNode } from 'react'
import { type Theme } from './themes'
import { applyTheme, getStoredTheme } from './applyTheme'

interface ThemeContextValue {
  theme: Theme
  setTheme: (t: Theme) => void
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(getStoredTheme)

  useEffect(() => {
    applyTheme(theme)
  }, [theme])

  function setTheme(t: Theme) {
    applyTheme(t)
    setThemeState(t)
  }

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error('useTheme must be used within ThemeProvider')
  return ctx
}
```

**Key implementation note**: `useState<Theme>(getStoredTheme)` uses the function form — `getStoredTheme` (no `()`) — so it reads localStorage only once on mount, not on every render. The `useEffect` runs `applyTheme` on initial render to reconcile the DOM `data-theme` attribute with the React state (important in case the HTML attribute differs from localStorage).

### §4 — `themes.css` (exact CSS)

```css
/* web/src/styles/themes.css */

[data-theme='default-light'] {
  --background: oklch(1 0 0);
  --foreground: oklch(0.145 0 0);
  --card: oklch(1 0 0);
  --card-foreground: oklch(0.145 0 0);
  --popover: oklch(1 0 0);
  --popover-foreground: oklch(0.145 0 0);
  --primary: oklch(0.205 0 0);
  --primary-foreground: oklch(0.985 0 0);
  --secondary: oklch(0.97 0 0);
  --secondary-foreground: oklch(0.205 0 0);
  --muted: oklch(0.97 0 0);
  --muted-foreground: oklch(0.556 0 0);
  --accent: oklch(0.97 0 0);
  --accent-foreground: oklch(0.205 0 0);
  --destructive: oklch(0.577 0.245 27.325);
  --border: oklch(0.922 0 0);
  --input: oklch(0.922 0 0);
  --ring: oklch(0.708 0 0);
  --chart-1: oklch(0.87 0 0);
  --chart-2: oklch(0.556 0 0);
  --chart-3: oklch(0.439 0 0);
  --chart-4: oklch(0.371 0 0);
  --chart-5: oklch(0.269 0 0);
  --radius: 0.625rem;
  --sidebar: oklch(0.985 0 0);
  --sidebar-foreground: oklch(0.145 0 0);
  --sidebar-primary: oklch(0.205 0 0);
  --sidebar-primary-foreground: oklch(0.985 0 0);
  --sidebar-accent: oklch(0.97 0 0);
  --sidebar-accent-foreground: oklch(0.205 0 0);
  --sidebar-border: oklch(0.922 0 0);
  --sidebar-ring: oklch(0.708 0 0);
}

[data-theme='slate-dark'] {
  --background: oklch(0.145 0 0);
  --foreground: oklch(0.985 0 0);
  --card: oklch(0.205 0 0);
  --card-foreground: oklch(0.985 0 0);
  --popover: oklch(0.205 0 0);
  --popover-foreground: oklch(0.985 0 0);
  --primary: oklch(0.922 0 0);
  --primary-foreground: oklch(0.205 0 0);
  --secondary: oklch(0.269 0 0);
  --secondary-foreground: oklch(0.985 0 0);
  --muted: oklch(0.269 0 0);
  --muted-foreground: oklch(0.708 0 0);
  --accent: oklch(0.269 0 0);
  --accent-foreground: oklch(0.985 0 0);
  --destructive: oklch(0.704 0.191 22.216);
  --border: oklch(1 0 0 / 10%);
  --input: oklch(1 0 0 / 15%);
  --ring: oklch(0.556 0 0);
  --chart-1: oklch(0.87 0 0);
  --chart-2: oklch(0.556 0 0);
  --chart-3: oklch(0.439 0 0);
  --chart-4: oklch(0.371 0 0);
  --chart-5: oklch(0.269 0 0);
  --sidebar: oklch(0.205 0 0);
  --sidebar-foreground: oklch(0.985 0 0);
  --sidebar-primary: oklch(0.488 0.243 264.376);
  --sidebar-primary-foreground: oklch(0.985 0 0);
  --sidebar-accent: oklch(0.269 0 0);
  --sidebar-accent-foreground: oklch(0.985 0 0);
  --sidebar-border: oklch(1 0 0 / 10%);
  --sidebar-ring: oklch(0.556 0 0);
}

[data-theme='solarized'] {
  /* Solarized Light palette — Ethan Schoonover */
  --background: oklch(0.97 0.024 91);        /* Base3  #fdf6e3 */
  --foreground: oklch(0.25 0.058 232);        /* Base03 #002b36 */
  --card: oklch(0.93 0.028 86);              /* Base2  #eee8d5 */
  --card-foreground: oklch(0.25 0.058 232);
  --popover: oklch(0.93 0.028 86);
  --popover-foreground: oklch(0.25 0.058 232);
  --primary: oklch(0.57 0.125 253);          /* Blue   #268bd2 */
  --primary-foreground: oklch(0.985 0 0);
  --secondary: oklch(0.93 0.028 86);
  --secondary-foreground: oklch(0.25 0.058 232);
  --muted: oklch(0.93 0.028 86);
  --muted-foreground: oklch(0.44 0.042 230); /* Base01 #586e75 */
  --accent: oklch(0.61 0.088 186);           /* Cyan   #2aa198 */
  --accent-foreground: oklch(0.985 0 0);
  --destructive: oklch(0.577 0.245 27.325);
  --border: oklch(0.64 0.018 220);           /* Base1  #93a1a1 */
  --input: oklch(0.64 0.018 220);
  --ring: oklch(0.57 0.125 253);
  --chart-1: oklch(0.60 0.107 84);           /* Yellow #b58900 */
  --chart-2: oklch(0.61 0.088 186);          /* Cyan   #2aa198 */
  --chart-3: oklch(0.57 0.125 253);          /* Blue   #268bd2 */
  --chart-4: oklch(0.56 0.140 22);           /* Orange #cb4b16 */
  --chart-5: oklch(0.52 0.131 333);          /* Magenta #d33682 */
  --radius: 0.625rem;
  --sidebar: oklch(0.93 0.028 86);
  --sidebar-foreground: oklch(0.25 0.058 232);
  --sidebar-primary: oklch(0.57 0.125 253);
  --sidebar-primary-foreground: oklch(0.985 0 0);
  --sidebar-accent: oklch(0.61 0.088 186);
  --sidebar-accent-foreground: oklch(0.985 0 0);
  --sidebar-border: oklch(0.64 0.018 220);
  --sidebar-ring: oklch(0.57 0.125 253);
}
```

**CSS cascade**: `[data-theme='...']` has specificity `[0, 1, 0]` — same as a class selector. Because the attribute is on `<html>`, all elements inside inherit the custom property values. The `@theme inline {}` block in `index.css` creates Tailwind aliases (e.g. `bg-background` → `var(--color-background)` → `var(--background)`), so switching `data-theme` on `<html>` instantly re-resolves all Tailwind color utilities in the entire tree.

### §5 — Updated `__root.tsx`

```tsx
// web/src/routes/__root.tsx
import { createRootRouteWithContext, Outlet } from '@tanstack/react-router'
import type { QueryClient } from '@tanstack/react-query'
import { Toaster } from 'sonner'
import { ThemeProvider } from '../lib/theme/ThemeProvider'

interface RouterContext {
  queryClient: QueryClient
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
})

function RootLayout() {
  return (
    <ThemeProvider>
      <Outlet />
      <Toaster richColors position="bottom-right" />
    </ThemeProvider>
  )
}
```

`ThemeProvider` wraps `<Outlet>` so the context is available in all routes (including `/login`). `<Toaster>` stays outside `ThemeProvider`'s JSX return — it must remain inside `ThemeProvider` to respect the theme CSS variables. Order: ThemeProvider → Outlet + Toaster.

### §6 — Theme selector JSX for `_app.tsx`

Add to the `<header>` in `AppLayout`, before the logout button:

```tsx
// At the top of _app.tsx, add these imports:
import { useTheme } from '../lib/theme/ThemeProvider'
import { THEMES, THEME_LABELS } from '../lib/theme/themes'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../components/ui/select'

// Inside AppLayout():
const { theme, setTheme } = useTheme()

// Inside the <header> JSX (before the logout button):
<Select value={theme} onValueChange={(v) => setTheme(v as Theme)}>
  <SelectTrigger
    className="w-[160px]"
    aria-label={t('theme.label')}
  >
    <SelectValue />
  </SelectTrigger>
  <SelectContent>
    {THEMES.map((t) => (
      <SelectItem key={t} value={t}>
        {t('theme.label')}  {/* see note below */}
      </SelectItem>
    ))}
  </SelectContent>
</Select>
```

**Note on i18n inside the map**: The variable `t` from `useTranslation()` conflicts with the map parameter name `t`. Rename the map parameter:

```tsx
{THEMES.map((themeName) => (
  <SelectItem key={themeName} value={themeName}>
    {t(THEME_LABELS[themeName])}
  </SelectItem>
))}
```

Also import `type { Theme }` from `'../lib/theme/themes'` for the cast in `onValueChange`.

### §7 — i18n keys (`en.json`)

Add this `"theme"` section at the top level of `en.json`:

```json
"theme": {
  "label": "Theme",
  "defaultLight": "Default Light",
  "slateDark": "Slate Dark",
  "solarized": "Solarized"
}
```

### §8 — Test outlines

**`web/src/lib/theme/__tests__/theme.test.ts`**

```typescript
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { applyTheme, getStoredTheme } from '../applyTheme'
import { DEFAULT_THEME } from '../themes'

describe('applyTheme', () => {
  beforeEach(() => localStorage.clear())

  it('sets data-theme attribute on <html>', () => {
    applyTheme('slate-dark')
    expect(document.documentElement.getAttribute('data-theme')).toBe('slate-dark')
  })

  it('writes ff-theme to localStorage', () => {
    applyTheme('solarized')
    expect(localStorage.getItem('ff-theme')).toBe('solarized')
  })
})

describe('getStoredTheme', () => {
  beforeEach(() => localStorage.clear())

  it('returns DEFAULT_THEME when localStorage is empty', () => {
    expect(getStoredTheme()).toBe(DEFAULT_THEME)
  })

  it('returns stored valid theme', () => {
    localStorage.setItem('ff-theme', 'slate-dark')
    expect(getStoredTheme()).toBe('slate-dark')
  })

  it('returns DEFAULT_THEME for unknown stored value', () => {
    localStorage.setItem('ff-theme', 'hacker-green')
    expect(getStoredTheme()).toBe(DEFAULT_THEME)
  })
})
```

**`web/src/lib/theme/__tests__/ThemeProvider.test.tsx`**

```tsx
import { render, screen, act } from '@testing-library/react'
import { describe, it, expect, beforeEach } from 'vitest'
import { ThemeProvider, useTheme } from '../ThemeProvider'
import { DEFAULT_THEME } from '../themes'

function ThemeDisplay() {
  const { theme, setTheme } = useTheme()
  return (
    <>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme('solarized')}>Switch</button>
    </>
  )
}

describe('ThemeProvider', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
  })

  it('applies DEFAULT_THEME on first render when localStorage is empty', () => {
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    expect(screen.getByTestId('theme').textContent).toBe(DEFAULT_THEME)
    expect(document.documentElement.getAttribute('data-theme')).toBe(DEFAULT_THEME)
  })

  it('restores stored theme from localStorage on mount', () => {
    localStorage.setItem('ff-theme', 'slate-dark')
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    expect(screen.getByTestId('theme').textContent).toBe('slate-dark')
  })

  it('setTheme updates data-theme attribute and localStorage', async () => {
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    await act(async () => screen.getByRole('button').click())
    expect(document.documentElement.getAttribute('data-theme')).toBe('solarized')
    expect(localStorage.getItem('ff-theme')).toBe('solarized')
  })
})
```

Tests use the Vitest/jsdom setup already configured in `web/src/test/setup.ts` — no additional setup needed. `localStorage` is available in jsdom.

### §9 — Critical Do-Nots

- **Do NOT add an inline `<script>` to `index.html`** in Story 7.2. The no-flash inline script (with CSP nonce injection from `IndexHtmlRewriter.cs`) is Story 7.3's responsibility. Story 7.2 uses `data-theme="default-light"` on `<html>` as a static fallback.
- **Do NOT remove the `@theme inline {}` block** from `index.css`. This block creates the Tailwind utility aliases (e.g. `--color-background: var(--background)`). Removing it breaks all `bg-background`, `text-foreground`, etc. Tailwind classes.
- **Do NOT remove the `@layer base {}` block** from `index.css`. It applies `border-border`, `bg-background`, `text-foreground`, and `font-sans` to base elements. These are required for the app to render correctly.
- **Do NOT add `.dark` class to the `<html>` element** anywhere. The `.dark` CSS class block is removed from `index.css` in Task 5; `@custom-variant dark` now activates via `[data-theme='slate-dark']`. Adding `.dark` class would do nothing useful and would confuse future maintainers.
- **Do NOT run `npx shadcn@latest add select`** — the shadcn `<Select>` is already at `web/src/components/ui/select.tsx`. Running the CLI would overwrite it.
- **Do NOT create a separate `ThemeSelector.tsx` component** — the selector is simple enough to inline in `_app.tsx`. Creating a separate file is premature abstraction for 3 lines of JSX.
- **Do NOT use `document.body.classList.toggle('dark')`** — Story 7.2 uses `data-theme` attributes, not CSS classes.
- **Do NOT put `ThemeProvider` inside `_app.tsx`** — it must wrap the root in `__root.tsx` so the login page also respects the stored theme.

### §10 — Architecture Compliance

| AC | Architecture Reference | How satisfied |
|----|----------------------|---------------|
| AC-1, AC-2 | AR-27 / Decision 4.2: "Tailwind 4 CSS variables keyed off `[data-theme='...']`" | `themes.css` uses `[data-theme='...']` selectors; `applyTheme` sets the attribute |
| AC-2 | Decision 4.2: "sets `data-theme` attribute before React" | `data-theme="default-light"` in HTML + ThemeProvider reconciles on mount |
| AC-3 | FR-38: "3 themes, persistence" | localStorage read on mount, React context propagates across navigations |
| File paths | Architecture §4.6 folder structure | `web/src/lib/theme/`, `web/src/styles/themes.css` match the architecture spec exactly |

### §11 — File Locations

| Action | Path |
|--------|------|
| CREATE | `web/src/lib/theme/themes.ts` |
| CREATE | `web/src/lib/theme/applyTheme.ts` |
| CREATE | `web/src/lib/theme/ThemeProvider.tsx` |
| CREATE | `web/src/styles/themes.css` |
| CREATE | `web/src/lib/theme/__tests__/theme.test.ts` |
| CREATE | `web/src/lib/theme/__tests__/ThemeProvider.test.tsx` |
| MODIFY | `web/src/index.css` — import themes.css, update `@custom-variant dark`, delete `:root` and `.dark` blocks |
| MODIFY | `web/index.html` — add `data-theme="default-light"` to `<html>` |
| MODIFY | `web/src/routes/__root.tsx` — wrap with `ThemeProvider` |
| MODIFY | `web/src/routes/_app.tsx` — add theme selector to header |
| MODIFY | `web/src/lib/i18n/locales/en.json` — add `"theme"` section |

### §12 — Previous Story Learnings (from Story 7.1)

- The `@/` alias resolves to `web/src/` (configured in `vite.config.ts`). Use `@/components/ui/select` NOT relative `../../../components/ui/select` for shadcn imports in `_app.tsx`. However, `_app.tsx` uses relative imports for lib and features (`../lib/...`, `../features/...`) consistently — follow the existing pattern in that file.
- 136/138 Vitest tests pass — 2 pre-existing failures in `ReorderableMenuList.test.tsx` are unrelated and pre-date Story 7.1. Do not be alarmed if these appear in test output.
- The shadcn `<Select>` component (`web/src/components/ui/select.tsx`) was installed previously and does NOT need re-installation.
- `web/src/styles/` directory does NOT exist yet — create it. The `index.css` file imports it as `@import './styles/themes.css'` (relative from `src/`).

---

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- ThemeProvider tests initially failed with "Found multiple elements" because the project uses manual `afterEach(() => cleanup())` — auto-cleanup is not registered. Fixed by adding explicit `afterEach(cleanup)` import pattern matching `Navbar.test.tsx`.

### Completion Notes List

- Created full theme infrastructure: `themes.ts` (constants/types), `applyTheme.ts` (DOM + localStorage), `ThemeProvider.tsx` (React context + hook).
- Created `web/src/styles/themes.css` with three `[data-theme='...']` blocks: `default-light` (former `:root`), `slate-dark` (former `.dark`), and `solarized` (Solarized Light palette).
- Updated `index.css`: added `@import './styles/themes.css'`, updated `@custom-variant dark` to use `[data-theme='slate-dark']`, removed `:root` and `.dark` blocks.
- Set `data-theme="default-light"` on `<html>` in `index.html` as static fallback before React mounts.
- Wired `ThemeProvider` at root in `__root.tsx` to cover all routes including login.
- Added `<Select>` theme picker to `_app.tsx` header using `useTheme`, `THEMES`, `THEME_LABELS`, and shadcn `<Select>` (pre-installed).
- Added `"theme"` i18n section to `en.json` with label, defaultLight, slateDark, solarized keys.
- 5 new tests pass (2 in `theme.test.ts` for `applyTheme`/`getStoredTheme`, 3 in `ThemeProvider.test.tsx`). 144/146 total pass; 2 pre-existing `ReorderableMenuList.test.tsx` failures unchanged.

### File List

- `web/src/lib/theme/themes.ts` (created)
- `web/src/lib/theme/applyTheme.ts` (created)
- `web/src/lib/theme/ThemeProvider.tsx` (created)
- `web/src/styles/themes.css` (created)
- `web/src/lib/theme/__tests__/theme.test.ts` (created)
- `web/src/lib/theme/__tests__/ThemeProvider.test.tsx` (created)
- `web/src/index.css` (modified)
- `web/index.html` (modified)
- `web/src/routes/__root.tsx` (modified)
- `web/src/routes/_app.tsx` (modified)
- `web/src/lib/i18n/locales/en.json` (modified)

### Review Findings

- [x] [Review][Patch] `--radius` missing from `[data-theme='slate-dark']` block — corner radii collapse to 0 in slate-dark theme [`web/src/styles/themes.css` slate-dark block]
- [x] [Review][Patch] `applyTheme` called twice per `setTheme` — change `useEffect` dependency from `[theme]` to `[]` so the effect handles only mount reconciliation; the explicit call in `setTheme` covers user-initiated changes [`web/src/lib/theme/ThemeProvider.tsx:15`]
- [x] [Review][Patch] "restores stored theme" test missing `data-theme` DOM assertion — add `expect(document.documentElement.getAttribute('data-theme')).toBe('slate-dark')` [`web/src/lib/theme/__tests__/ThemeProvider.test.tsx:32-36`]
- [x] [Review][Defer] Flash of `default-light` before stored non-default theme applied on page reload [`web/index.html:2`, `web/src/lib/theme/ThemeProvider.tsx:15`] — deferred, Story 7.3 responsibility (inline script with CSP nonce injection via `IndexHtmlRewriter.cs`)
