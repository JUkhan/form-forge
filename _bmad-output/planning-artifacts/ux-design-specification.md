---
stepsCompleted: [1, 6, 8, 11, 13, 14]
lastStep: 14
status: complete
completedDate: 2026-05-31
inputDocuments:
  - web/src/index.css
  - web/src/styles/themes.css
  - web/src/lib/theme/themes.ts
  - web/src/lib/theme/applyTheme.ts
  - web/src/lib/theme/ThemeProvider.tsx
scope: focused-token-spec
---

# UX Design Specification — FormForge Theme Token System

**Author:** jukhan
**Date:** 2026-05-31
**Scope:** Semantic CSS-variable token system for three themes (Default Light, Slate Dark, Solarized), three regions (left menu, header, body), and five component groups (buttons, icon buttons, breadcrumbs, tabs, forms). Remediation of `dark:`-variant + hardcoded-color breakage.

> **Workflow note.** This run used the *focused token spec* path of `bmad-create-ux-design`. Generic UX-discovery steps (audience, IA, emotional response, inspiration, user journeys) were intentionally skipped because FormForge already ships from Epic 7 — this is brownfield theming remediation, not greenfield product UX. The four relevant workflow steps are folded together below: **§2 Design-System Foundation (step 6)**, **§3 Visual Foundation / Tokens (step 8)**, **§5–6 Component & Region Strategy (step 11)**, **§7 Accessibility (step 13)**.

---

## 1. Problem Statement & Current-State Findings

FormForge's token *infrastructure already exists and is sound*; the breakage is in **how components consume it**.

**What already works (keep):**

- `web/src/styles/themes.css` defines full OKLCh token sets under three `[data-theme=…]` selectors: `default-light`, `slate-dark`, `solarized`.
- `web/src/index.css` `@theme inline { … }` maps every `--color-*` Tailwind utility to a `var(--*)` token, so `bg-background`, `text-foreground`, `border-border`, etc. are already theme-reactive.
- `data-theme` is correctly set on `<html>`: static fallback in `index.html`, no-flash bootstrap from `localStorage('ff-theme')`, runtime via `applyTheme.ts` → `document.documentElement.setAttribute('data-theme', …)`, React state via `ThemeProvider.tsx`. Theme IDs: `default-light` | `slate-dark` | `solarized` (`lib/theme/themes.ts`).

**Root causes of the breakage:**

1. **`index.css:7` — `@custom-variant dark (&:is([data-theme='slate-dark'] *))`.** This redefines Tailwind's `dark:` to fire **only** for Slate Dark. Every `dark:`-prefixed class is therefore invisible to Solarized, and silently irrelevant to Default Light. This single line is the architectural defect the brief calls out. **→ Decision: delete it (§2.1).**
2. **~19 files bypass tokens with hardcoded colors** — `bg-white`, `bg-slate-900`, `border-slate-200`, `text-slate-500/700`, `bg-slate-800`, `text-white`. These render correctly *only* under Default Light and visibly wrong under both other themes. Worst offenders: `ui/tooltip.tsx` (`bg-slate-900 text-white`), `ui/command.tsx`, `ui/popover.tsx`, `shared/Navbar.tsx` (entire sidebar), `routes/_app.tsx` (header), `dataEntry/*`, and `designer/DesignerCanvas.tsx` (50+ instances).
3. **Token-taxonomy gaps** vs. the brief's required categories — no explicit interactive-state tokens (hover/active currently faked via `/30`,`/50` opacity inside `dark:` only), no `--destructive-foreground`, no dedicated form-field surface tokens, no `--ring-offset`, no header-region tokens.
4. **Latent WCAG failures already baked into the tokens** (independent of `dark:`), surfaced by audit in §7: Solarized white-on-`--primary`/`--accent` buttons fail AA; Slate-Dark form-field borders (`white/15%`) are effectively invisible at 1.47:1.

---

## 2. Design-System Foundation

### 2.1 Architectural decisions

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | **Remove the `@custom-variant dark` line entirely.** Themes are selected *only* via `data-theme`; there is no "dark mode" axis. | Slate Dark is one of three peer themes, not a light/dark toggle. A `dark:` variant fundamentally cannot express a 3-theme system. |
| D2 | **Single semantic token layer.** Components reference *only* semantic tokens (`bg-background`, `text-muted-foreground`, `border-input`, `bg-field`, …). They never reference a theme name, a raw palette color (`slate-*`, `white`), or a `dark:` variant. | One indirection: components → semantic token → per-theme value. New themes = one new `[data-theme]` block, zero component edits. |
| D3 | **Tokens are defined per theme in `themes.css`; the Tailwind mapping lives once in `@theme inline`.** New tokens added in §3 follow the existing pattern. | Preserves the working infrastructure; additive, low-risk. |
| D4 | **Interactive states are derived, not hand-set per theme,** via `color-mix` overlay tokens keyed off `--foreground` / `--*-foreground` (§3.4). | Hover/active stay correct in every theme automatically; avoids 3× hand-tuning and the opacity-hack that only worked in `dark:`. |
| D5 | **shadcn/ui remains the component substrate.** This is a themeable-system approach (Tailwind v4 + shadcn + CSS variables), not a custom or third-party design system. | Components already exist; the work is wiring them to tokens, not replacing them. |

### 2.2 Token naming convention

`--<role>` for surfaces/text/lines, `--<role>-foreground` for the text/icon color that sits *on* that surface, `--<role>-hover` / `--<role>-active` for interactive deltas. Tailwind utility = `--color-<role>` → `bg-<role>` / `text-<role>` / `border-<role>`. This matches the existing shadcn convention already in the file.

---

## 3. Visual Foundation — The Token System

Seven semantic groups, exactly per the brief: **surfaces · text · borders · accent/brand · interactive states · focus ring · form fields.**

### 3.1 Token taxonomy (theme-independent contract)

| Group | Token | Meaning | Status |
|-------|-------|---------|--------|
| **Surfaces** | `--background` | App canvas / body region | exists |
| | `--card` | Raised panels, content cards | exists |
| | `--popover` | Floating surfaces (menus, tooltips, popovers) | exists |
| | `--muted` | Subdued fill (chips, inactive tab strip) | exists |
| | `--sidebar` | Left menu region surface | exists |
| **Text** | `--foreground` | Primary text on `--background` | exists |
| | `--card-foreground` / `--popover-foreground` | Text on those surfaces | exists |
| | `--muted-foreground` | Secondary/placeholder/caption text | exists |
| | `--sidebar-foreground` | Text in left menu | exists |
| **Borders** | `--border` | Decorative dividers, card edges | exists |
| | `--input` | *(repurposed →)* generic control border | exists |
| | `--field-border` | **Form-field boundary (affordance-bearing, ≥3:1)** | **NEW** |
| | `--sidebar-border` | Left-menu dividers | exists |
| **Accent / brand** | `--primary` / `--primary-foreground` | Primary action color + its text | exists *(Solarized value fixed, §7)* |
| | `--secondary` / `--secondary-foreground` | Secondary action | exists |
| | `--accent` / `--accent-foreground` | Accent/highlight, active-nav tint | exists *(Solarized value fixed, §7)* |
| | `--destructive` | Danger action/border | exists *(Slate-Dark value fixed, §7)* |
| | `--destructive-foreground` | **Text/icon on `--destructive`** | **NEW** |
| **Interactive** | `--overlay-hover` | Hover veil for ghost/transparent controls | **NEW** |
| | `--overlay-active` | Pressed veil | **NEW** |
| | `--primary-hover` / `--primary-active` | Solid primary button states | **NEW** |
| | `--accent-hover` / `--accent-active` | Solid accent states | **NEW** |
| **Focus ring** | `--ring` | Focus indicator color | exists |
| | `--ring-offset` | Gap color behind the ring (= surface) | **NEW** |
| **Form fields** | `--field` | Input/select/textarea fill | **NEW** |
| | `--field-foreground` | Typed value text (= `--foreground`) | **NEW (alias)** |
| | `--placeholder` | Placeholder text (= `--muted-foreground`) | **NEW (alias)** |

### 3.2 Per-theme values — existing tokens (verbatim from `themes.css`, unchanged)

| Token | Default Light | Slate Dark | Solarized |
|-------|---------------|-----------|-----------|
| `--background` | `oklch(1 0 0)` | `oklch(0.145 0 0)` | `oklch(0.97 0.024 91)` |
| `--foreground` | `oklch(0.145 0 0)` | `oklch(0.985 0 0)` | `oklch(0.25 0.058 232)` |
| `--card` | `oklch(1 0 0)` | `oklch(0.205 0 0)` | `oklch(0.93 0.028 86)` |
| `--card-foreground` | `oklch(0.145 0 0)` | `oklch(0.985 0 0)` | `oklch(0.25 0.058 232)` |
| `--popover` | `oklch(1 0 0)` | `oklch(0.205 0 0)` | `oklch(0.93 0.028 86)` |
| `--muted` | `oklch(0.97 0 0)` | `oklch(0.269 0 0)` | `oklch(0.93 0.028 86)` |
| `--muted-foreground` | `oklch(0.556 0 0)` | `oklch(0.708 0 0)` | `oklch(0.44 0.042 230)` |
| `--secondary` | `oklch(0.97 0 0)` | `oklch(0.269 0 0)` | `oklch(0.93 0.028 86)` |
| `--secondary-foreground` | `oklch(0.205 0 0)` | `oklch(0.985 0 0)` | `oklch(0.25 0.058 232)` |
| `--primary-foreground` | `oklch(0.985 0 0)` | `oklch(0.205 0 0)` | `oklch(0.985 0 0)` |
| `--border` | `oklch(0.922 0 0)` | `oklch(1 0 0 / 10%)` | `oklch(0.64 0.018 220)` |
| `--ring` | `oklch(0.708 0 0)` | `oklch(0.556 0 0)` | `oklch(0.57 0.125 253)` |
| `--sidebar` | `oklch(0.985 0 0)` | `oklch(0.205 0 0)` | `oklch(0.93 0.028 86)` |
| `--sidebar-foreground` | `oklch(0.145 0 0)` | `oklch(0.985 0 0)` | `oklch(0.25 0.058 232)` |
| `--sidebar-border` | `oklch(0.922 0 0)` | `oklch(1 0 0 / 10%)` | `oklch(0.64 0.018 220)` |

### 3.3 Per-theme values — CHANGED tokens (WCAG fixes, see §7 for ratios)

| Token | Default Light | Slate Dark | Solarized | Why |
|-------|---------------|-----------|-----------|-----|
| `--primary` | `oklch(0.205 0 0)` *(unch.)* | `oklch(0.922 0 0)` *(unch.)* | **`oklch(0.50 0.13 253)`** ← was `0.57` | white-on-primary was 4.28:1 → now **5.77:1** |
| `--accent` | `oklch(0.97 0 0)` *(unch.)* | `oklch(0.269 0 0)` *(unch.)* | **`oklch(0.52 0.09 186)`** ← was `0.61` | white-on-accent was 3.48:1 → now **5.03:1** |
| `--destructive` | `oklch(0.577 0.245 27.325)` *(unch.)* | **`oklch(0.55 0.22 25)`** ← was `0.704` | `oklch(0.577 0.245 27.325)` *(unch.)* | white-on-destructive (dark) was 2.77:1 → now **5.21:1** |

> Solarized `--accent` darkening trades a little of Schoonover's exact cyan for AA-compliant white text. If brand-exact cyan is mandatory, the alternative is `--accent-foreground: oklch(0.25 0.058 232)` (dark text on bright cyan) — flag for stakeholder choice in §7.

### 3.4 Per-theme values — NEW tokens

| Token | Default Light | Slate Dark | Solarized |
|-------|---------------|-----------|-----------|
| `--destructive-foreground` | `oklch(0.985 0 0)` | `oklch(0.985 0 0)` | `oklch(0.985 0 0)` |
| `--field` | `oklch(1 0 0)` | `oklch(0.24 0 0)` | `oklch(0.95 0.02 86)` |
| `--field-border` | `oklch(0.65 0 0)` | `oklch(0.53 0 0)` | `oklch(0.58 0.02 220)` |
| `--ring-offset` | `var(--background)` | `var(--background)` | `var(--background)` |

These overlay + alias tokens are **defined once** (not per theme) because they reference per-theme tokens, so they adapt automatically. Add to `:root` (or an `@layer base` block):

```css
:root {
  --field-foreground: var(--foreground);
  --placeholder:      var(--muted-foreground);
  /* Interactive overlays — direction-correct in every theme because they
     pull toward each theme's own --foreground. */
  --overlay-hover:  color-mix(in oklab, var(--foreground) 8%,  transparent);
  --overlay-active: color-mix(in oklab, var(--foreground) 14%, transparent);
  /* Solid-button states pull toward the button's own text color. */
  --primary-hover:  color-mix(in oklab, var(--primary) 90%, var(--primary-foreground));
  --primary-active: color-mix(in oklab, var(--primary) 84%, var(--primary-foreground));
  --accent-hover:   color-mix(in oklab, var(--accent)  90%, var(--accent-foreground));
  --accent-active:  color-mix(in oklab, var(--accent)  84%, var(--accent-foreground));
}
```

### 3.5 Required `@theme inline` additions

Append to the existing block in `index.css` so the new tokens become Tailwind utilities (`bg-field`, `border-field-border`, `text-destructive-foreground`, `bg-overlay-hover`, etc.):

```css
--color-destructive-foreground: var(--destructive-foreground);
--color-field:            var(--field);
--color-field-border:     var(--field-border);
--color-placeholder:      var(--placeholder);
--color-overlay-hover:    var(--overlay-hover);
--color-overlay-active:   var(--overlay-active);
--color-primary-hover:    var(--primary-hover);
--color-primary-active:   var(--primary-active);
--color-accent-hover:     var(--accent-hover);
--color-accent-active:    var(--accent-active);
```

---

## 4. Interactive-State Model (hover / active / focus / disabled)

One convention, applied uniformly; never a `dark:` branch.

| State | Mechanism | Token(s) | Notes |
|-------|-----------|----------|-------|
| **Rest** | base surface + foreground | per component (§5) | — |
| **Hover** | *Solid* controls swap to `--*-hover`; *ghost/transparent* controls overlay `--overlay-hover` on top of their rest surface | `--primary-hover`, `--accent-hover`, `--overlay-hover` | overlay = 8% foreground veil; works on any surface |
| **Active/Pressed** | swap to `--*-active` / overlay `--overlay-active` | `--primary-active`, `--accent-active`, `--overlay-active` | 14% veil |
| **Focus-visible** | 2px ring in `--ring` + 2px offset in `--ring-offset` | `--ring`, `--ring-offset` | `outline: 2px solid var(--ring); outline-offset: 2px;` *(or shadcn `focus-visible:ring-[3px] ring-ring`)*. Keyboard-only via `:focus-visible`. |
| **Disabled** | `opacity: 0.5; cursor: not-allowed; pointer-events: none` | none | theme-agnostic; matches existing shadcn `disabled:opacity-50`. No color token needed. |
| **Invalid** (forms) | border + ring swap to destructive | `--destructive`, `--ring` → destructive | replaces `aria-invalid:*` `dark:` pairs |

---

## 5. Component → Token Mapping

All five brief components, plus the supporting controls found in the audit. "Remove" = delete that hardcoded class / `dark:` pair.

### 5.1 Button — `ui/button.tsx`
| Variant | Surface | Text | Hover | Active | Focus | Disabled |
|---------|---------|------|-------|--------|-------|----------|
| default (primary) | `bg-primary` | `text-primary-foreground` | `bg-primary-hover` | `bg-primary-active` | ring `--ring` | opacity-50 |
| secondary | `bg-secondary` | `text-secondary-foreground` | `+ bg-overlay-hover` | `+ bg-overlay-active` | ring | opacity-50 |
| outline | `bg-background border-border` | `text-foreground` | `bg-overlay-hover` | `bg-overlay-active` | ring | opacity-50 |
| ghost | transparent | `text-foreground` | `bg-overlay-hover` | `bg-overlay-active` | ring | opacity-50 |
| destructive | `bg-destructive` | `text-destructive-foreground` | overlay-active veil | — | ring | opacity-50 |
| **Remove** | `dark:border-input`, `dark:bg-input/30`, `dark:hover:bg-input/50`, `dark:hover:bg-muted/50`, `dark:bg-destructive/*`, `dark:aria-invalid:*` (lines 8,14,18,20) | | | | | |

### 5.2 Icon button — `ui/button.tsx` `size="icon"` + ad-hoc icon triggers
Same token set as Button ghost/outline. Square hit-area ≥ `size-9` (36px; bump to `size-11`/44px for primary touch targets, §7). Icon color inherits `currentColor` from `text-foreground` / `text-muted-foreground`; **never** `text-slate-*`. Fixes: `SearchBox.tsx:43` (`text-slate-400` → `text-muted-foreground`), `Navbar.tsx:267` icon colors, `SortHeader.tsx:37`.

### 5.3 Breadcrumbs
| Part | Token |
|------|-------|
| separator (`/`, chevron) | `text-muted-foreground` |
| inactive crumb link | `text-muted-foreground` → hover `text-foreground` (+ `bg-overlay-hover` if chip-style) |
| current crumb (`aria-current="page"`) | `text-foreground` |
| focus | ring `--ring` |

### 5.4 Tabs
| Part | Token |
|------|-------|
| tab strip background | `bg-muted` (or transparent) |
| inactive tab | `text-muted-foreground`, hover `bg-overlay-hover` |
| active tab | `text-foreground`, surface `bg-background`/`bg-card`, indicator `bg-primary` (or `border-primary`) |
| focus | ring `--ring` |
| disabled tab | opacity-50 |

### 5.5 Forms — `ui/input.tsx`, `ui/textarea.tsx`, `ui/select.tsx`, `ui/checkbox.tsx`, `ui/label.tsx`
| Part | Token |
|------|-------|
| field fill | `bg-field` |
| field border (rest) | `border-field-border` |
| field border (hover) | `border-foreground/40` *(or `border-ring`)* |
| typed text | `text-field-foreground` (= `text-foreground`) |
| placeholder | `placeholder:text-placeholder` (= muted-foreground) |
| label | `text-foreground`; optional/help text `text-muted-foreground` |
| focus | `border-ring` + 2px ring `--ring`, offset `--ring-offset` |
| invalid | `border-destructive` + ring→destructive, message `text-destructive` |
| disabled | opacity-50, `bg-muted`, `cursor-not-allowed` |
| checkbox checked | `bg-primary border-primary`, check `text-primary-foreground` |
| **Remove** | every `dark:bg-input/30`, `dark:disabled:bg-input/80`, `dark:hover:bg-input/50`, `dark:aria-invalid:*` across input/textarea/select/checkbox |

### 5.6 Other audited controls (same rules, hardcode → token)
| File | Hardcoded → Token |
|------|-------------------|
| `ui/tooltip.tsx` | `bg-slate-900`→`bg-popover`, `text-white`→`text-popover-foreground`, `fill-slate-900`→`fill-popover` |
| `ui/popover.tsx` | `border-slate-200`→`border-border`, add `bg-popover text-popover-foreground` |
| `ui/command.tsx` | `border-slate-200`→`border-border`, `text-slate-400/500`→`text-muted-foreground` |
| `ui/combobox.tsx` | `text-slate-500`→`text-muted-foreground` |
| `shared/ErrorBanner.tsx` | `bg-red-50/border-red-200/text-red-800`→`bg-destructive/10 border-destructive/30 text-destructive` |
| `designer/RepeaterRowDrawer.tsx` | `bg-slate-800 text-white hover:bg-slate-700`→`bg-primary text-primary-foreground hover:bg-primary-hover` |

---

## 6. Region → Token Mapping

### 6.1 Left menu panel — `shared/Navbar.tsx`
| Element | Token |
|---------|-------|
| panel surface | `bg-sidebar` |
| right divider / brand divider | `border-sidebar-border` |
| menu item text (rest) | `text-sidebar-foreground/80` or `text-muted-foreground` |
| menu item hover | `bg-overlay-hover` + `text-sidebar-foreground` |
| menu item active (current route) | `bg-sidebar-accent text-sidebar-accent-foreground` (indicator `bg-sidebar-primary`) |
| icons | `currentColor` (inherit) |
| focus | ring `--sidebar-ring` |
| **Remove** | all `bg-white`, `border-slate-200`, `text-slate-500/700`, `hover:bg-slate-50/100`, mobile-drawer `border-slate-200 bg-white text-slate-700` (lines 49,61–63,80–81,87,150–151,161,187–189,267) |

### 6.2 Top header — `routes/_app.tsx` (AppLayout)
| Element | Token |
|---------|-------|
| header bar surface | `bg-background` (or `bg-card` for separation) |
| bottom divider | `border-border` |
| user display name | `text-foreground` *(was `text-slate-700`, line 108)* |
| avatar initials | `bg-primary/10 text-primary` *(already token-correct — keep)* |
| theme `<Select>` / logout `<Button>` | inherit component tokens (§5.1, §5.5) |
| **Remove** | `border-slate-200 bg-white` (line 97), `text-slate-700` (line 108) |

### 6.3 Main body — `dataEntry/*`, `designer/*`
| Element | Token |
|---------|-------|
| body canvas | `bg-background` |
| content cards / panels | `bg-card border-border text-card-foreground` |
| page-header bars | `bg-card border-border` (or `bg-background`) |
| primary headings | `text-foreground` *(was `text-slate-900`)* |
| secondary/meta text | `text-muted-foreground` *(was `text-slate-500`)* |
| badges | `bg-muted text-muted-foreground` *(was `bg-slate-200 text-slate-600`)* |
| designer canvas grid/dropzones | `bg-muted border-border`, hover `bg-overlay-hover` |
| designer toolbar panel | `bg-card border-border`, section headers `bg-muted` |
| **Remove** | every `bg-white`, `border-slate-200/300`, `text-slate-400/500/600/700/900`, `bg-slate-50/100/200` across `DataEntryPage.tsx`, `RecordDetailPage.tsx`, `DesignerCanvas.tsx` (50+), `DesignerToolbar.tsx`, `ElementRenderer.tsx`, `DynamicComponent.tsx` |

---

## 7. WCAG AA Contrast — Measured Results

Ratios computed from the actual OKLCh values (OKLCh→sRGB→relative-luminance→WCAG 2.x contrast). Thresholds: **4.5:1** normal text, **3:1** large text / non-text UI boundaries (WCAG 1.4.3 / 1.4.11). Decorative dividers are exempt (1.4.11) and are *not* failures.

### 7.1 Text & component pairs — all **PASS** after §3.3 fixes
| Pair | Default Light | Slate Dark | Solarized |
|------|--------------|-----------|-----------|
| foreground / background | 19.79 ✓ | 18.96 ✓ | 14.48 ✓ |
| muted-foreground / background | 4.73 ✓ | 7.63 ✓ | 7.04 ✓ |
| card-foreground / card | 19.79 ✓ | 17.16 ✓ | 12.84 ✓ |
| primary-fg / primary (button) | 17.16 ✓ | 14.22 ✓ | **5.77 ✓** *(was 4.28 ✗)* |
| secondary-fg / secondary | 16.42 ✓ | 14.48 ✓ | 12.84 ✓ |
| accent-fg / accent | 16.42 ✓ | 14.48 ✓ | **5.03 ✓** *(was 3.48 ✗)* |
| destructive-fg / destructive | 4.56 ✓ | **5.21 ✓** *(was 2.77 ✗)* | 4.56 ✓ |
| field text / field | 18.68 ✓ | 15.76 ✓ | 13.63 ✓ |
| placeholder / field | 4.73 ✓ | 6.34 ✓ | 6.63 ✓ |

### 7.2 Non-text UI (3:1) — focus ring & field boundary
| Pair | Default Light | Slate Dark | Solarized |
|------|--------------|-----------|-----------|
| ring / background | 2.59 ⚠ | 4.18 ✓ | 4.10 ✓ |
| field-border / field | 3.05 ✓ | 3.12 ✓ | 3.68 ✓ |

**Open item — Default-Light focus ring (2.59:1, below 3:1).** `--ring oklch(0.708 0 0)` is too light on white. Recommended fix: darken Default-Light `--ring` to `oklch(0.55 0 0)` (≈ 4.8:1) so the keyboard-focus indicator meets 1.4.11. (Decorative `--border` at ~1.26:1 is intentionally subtle and exempt; the *affordance-bearing* `--field-border` carries the 3:1 boundary instead.)

### 7.3 Stakeholder decision flagged
- **Solarized accent fidelity.** §3.3 darkens accent cyan to `0.52` for white-text AA. If exact Schoonover cyan (`0.61`) is required, switch `--accent-foreground` to dark (`oklch(0.25 0.058 232)`) instead. Pick one.

---

## 8. Migration Checklist (for the implementing dev story)

1. **Delete** `@custom-variant dark (…)` from `index.css:7`.
2. **Add** the new tokens: `--field`,`--field-border`,`--destructive-foreground`,`--ring-offset` per theme (§3.4) + the `:root` overlay/alias block + `@theme inline` mappings (§3.5).
3. **Apply** the three WCAG value fixes (§3.3) and the Default-Light `--ring` darken (§7.2).
4. **Sweep** the ~19 hardcoded-color files (§5–6 tables) — replace every `slate-*`/`white`/`red-*` and every `dark:` pair with the mapped token.
5. **Verify**: toggle all three themes across header, left menu, body, and each of the five components; run an automated contrast check + keyboard-focus pass (matches Story 7-4 accessibility scope).
6. **Guardrail** (optional): lint rule forbidding `dark:` and raw `bg-white`/`*-slate-*` in `web/src/components/**`.

---

<!-- end focused token spec -->
<!-- UX design content will be appended sequentially through collaborative workflow steps -->

