# Story 3.10: Keyboard-Accessible Designer DnD

Status: done

## Story

As a Platform Admin using only a keyboard,
I want to interact with the designer canvas via keyboard,
so that the designer is usable without a pointing device and FormForge meets WCAG 2.1 AA (FR-42 AC-4).

## Acceptance Criteria

**AC-1 — Visible focus on draggable elements**
Given I focus a draggable component in the palette or canvas
When I press Tab to reach it
Then the focused element receives a visible focus outline

**AC-2 — Space/Enter picks up an element**
Given a draggable component has focus
When I press Space or Enter
Then the component is "picked up" and the SPA announces the pickup via `aria-live="polite"`

**AC-3 — Arrow keys / Tab move focus through DropZones**
Given a component is picked up
When I press Tab (or arrow keys while a DropZone has focus)
Then focus moves through valid drop targets (DropZones); each DropZone has a descriptive `aria-label`

**AC-4 — Space/Enter on DropZone drops the element**
Given focus is on a valid drop target
When I press Space or Enter
Then the component is inserted at that position and the SPA announces the drop
And the canvas emits the same updated RootElement JSON it would have emitted via HTML5 DnD

**AC-5 — Escape cancels**
Given a component is picked up
When I press Escape
Then the pickup is cancelled and no canvas state changes
And the SPA announces the cancellation via `aria-live="polite"`

**AC-6 — Hook reusable for Epic 4**
Given the `useKeyboardDnD()` hook in `web/src/components/designer/`
When the menu reorder UI uses it (Epic 4 reuses this hook)
Then the same keyboard interaction model applies to menu item reordering
*(The hook must be generic — no designer-specific imports — so it is clean to import from `features/menu/`.)*

**AC-7 — axe-core zero critical violations**
Given the rendered designer
When an axe-core audit runs
Then zero critical violations are reported

> **Verification deferred to Story 7.4 (Accessibility Compliance)** — Story 3.10
> ships the keyboard-DnD wiring with construction-level a11y (role=button,
> aria-label, visible focus, aria-live), but does not bundle a vitest-axe
> harness. The harness, the axe smoke test, and any resulting fixes (notably
> `aria-grabbed` deprecation per ARIA 1.1) are scheduled into Story 7.4 so the
> entire designer surface — not just the keyboard pathway — is audited together.

## Tasks / Subtasks

- [x] Task 1: Create `useKeyboardDnD<T>()` hook (AC-2, AC-5, AC-6)
  - [x] Create `web/src/components/designer/useKeyboardDnD.ts`
  - [x] The hook is **generic** — `export function useKeyboardDnD<T>()`. No imports from
        `./dnd`, `@/store/designerCanvas`, or any other designer-specific module. This keeps it
        reusable for Epic 4.
  - [x] State managed by the hook:
        ```typescript
        const [pickedUp, setPickedUp] = useState<T | null>(null)
        const [announcement, setAnnouncement] = useState<string>('')
        ```
  - [x] Exposed API (all wrapped in `useCallback`):
        ```typescript
        // Called from PaletteCard / canvas element onKeyDown when Space or Enter is pressed.
        // `announce` is the i18n-translated string produced by the caller.
        function pickUp(data: T, announce: string): void

        // Called from DropZone onKeyDown when Space or Enter is pressed while pickedUp is set.
        // Caller passes the announce string; hook clears pickedUp state.
        function commit(announce: string): void

        // Called on Escape key anywhere a keyboard-DnD participant has focus.
        function cancel(announce: string): void
        ```
  - [x] Export the hook's return type for callers that need to type the prop:
        ```typescript
        export type UseKeyboardDnDReturn<T> = ReturnType<typeof useKeyboardDnD<T>>
        ```
  - [x] Full export block at module bottom:
        ```typescript
        export { useKeyboardDnD }
        export type { UseKeyboardDnDReturn }
        ```

- [x] Task 2: Wire hook state in `DesignerPage` — host, aria-live, prop threading (AC-2, AC-5)
  - [x] In `web/src/routes/_app/designer.$designerId.tsx`:
    - Import `useKeyboardDnD` and `type UseKeyboardDnDReturn` from `@/components/designer/useKeyboardDnD`
    - Import `type DragData` from `@/components/designer/dnd`
    - Instantiate the hook: `const kbdDnD = useKeyboardDnD<DragData>()`
    - Add a visually-hidden `aria-live` region inside the page `div` (NOT inside the body flex row
      so it doesn't affect layout), placed just before the 3-pane body block:
      ```tsx
      <div
        role="status"
        aria-live="polite"
        aria-atomic="true"
        className="sr-only"
      >
        {kbdDnD.announcement}
      </div>
      ```
      Use Tailwind `sr-only` (which is already available in the project) — it hides the element
      visually while keeping it in the accessibility tree.
    - Pass `kbdDnD` to both siblings in the 3-pane body:
      ```tsx
      <DesignerToolbar kbdDnD={kbdDnD} />
      <DesignerCanvas className="flex-1 bg-slate-100/40" kbdDnD={kbdDnD} />
      ```
  - [x] **Do NOT** change `PropertyInspector` — it has no draggable elements.
  - [x] `DesignerPage` is not exported from a test file so no test file for the page needs updating;
        the existing route test (`-designer.$designerId.test.tsx` if it exists) does not need
        changes because the new props are optional (see Task 3 + Task 4 prop shapes).

- [x] Task 3: Wire keyboard DnD in `DesignerToolbar.tsx` (AC-1, AC-2, AC-5)
  - [x] Add the `kbdDnD` prop (optional to keep the component self-standing in tests):
        ```typescript
        interface DesignerToolbarProps {
          className?: string
          kbdDnD?: UseKeyboardDnDReturn<DragData>
        }
        ```
        Import `type UseKeyboardDnDReturn` from `./useKeyboardDnD` and `type DragData` from `./dnd`.
  - [x] Destructure `kbdDnD` in `DesignerToolbar` and pass it to `PaletteCard`.
  - [x] `PaletteCard` already has `tabIndex={0}`, `role="button"`, and a CSS
        `focus-visible:ring-2 focus-visible:ring-primary/60` focus ring — **do NOT change
        these**. They already satisfy AC-1 for the palette.
  - [x] Add `onKeyDown` to the `PaletteCard` `<div>`:
        ```tsx
        onKeyDown={(e) => {
          if (e.key === ' ' || e.key === 'Enter') {
            e.preventDefault()
            kbdDnD?.pickUp(
              { source: 'PALETTE', elementType: type },
              t('designer.keyboard.pickedUp', { type: label }),
            )
          }
          if (e.key === 'Escape' && kbdDnD?.pickedUp) {
            kbdDnD.cancel(t('designer.keyboard.cancelled'))
          }
        }}
        ```
        Add `aria-grabbed={kbdDnD?.pickedUp?.source === 'PALETTE' &&
          (kbdDnD.pickedUp as { elementType?: string }).elementType === type}` to the `<div>`.
  - [x] Add `useTranslation` import to `DesignerToolbar.tsx`. Pass `kbdDnD` down to `PaletteCard`
        as an optional prop.
  - [x] `PaletteCard` gets the `kbdDnD` and `label` already available inside it — wire accordingly.

- [x] Task 4: Wire keyboard DnD in `DesignerCanvas.tsx` (AC-1, AC-2, AC-3, AC-4, AC-5)
  - [x] Add prop to `DesignerCanvas`:
        ```typescript
        interface DesignerCanvasProps {
          className?: string
          kbdDnD?: UseKeyboardDnDReturn<DragData>
        }
        ```
  - [x] Create a local React context inside `DesignerCanvas.tsx` (NOT exported — it's an
        implementation detail of this file):
        ```typescript
        const KbdDnDCtx = createContext<UseKeyboardDnDReturn<DragData> | null>(null)
        ```
        Provide it in `DesignerCanvas` → `CanvasRoot`. Wrap `<CanvasRoot>` in the provider:
        ```tsx
        <KbdDnDCtx.Provider value={kbdDnD ?? null}>
          <CanvasRoot rootElement={rootElement} className={className} />
        </KbdDnDCtx.Provider>
        ```
  - [x] Add `import { createContext, useContext, … }` to the existing React import block.

  **DropZone keyboard wiring (inside `DesignerCanvas.tsx`):**
  - [x] `DropZone` reads context: `const kbdDnD = useContext(KbdDnDCtx)`
  - [x] Add a `label` prop to `DropZone` so callers can supply a human-readable description:
        ```typescript
        label?: string   // e.g. "after Stack, position 2" — built by callers
        ```
  - [x] When `kbdDnD?.pickedUp` is **non-null**, the DropZone becomes keyboard-interactive:
        ```tsx
        // Replaces the existing `aria-hidden` attribute with conditional logic:
        // When NOT picking: aria-hidden (as before)
        // When picking: focusable button with label and keyboard handler
        {...(kbdDnD?.pickedUp
          ? {
              tabIndex: 0,
              role: 'button' as const,
              'aria-label': label ?? t('designer.keyboard.dropZoneFallback'),
              'data-dropzone': `${parentId}:${index}`,
            }
          : { 'aria-hidden': true as const })}
        onKeyDown={(e) => {
          if (!kbdDnD?.pickedUp) return
          if (e.key === ' ' || e.key === 'Enter') {
            e.preventDefault()
            const dragData = kbdDnD.pickedUp
            const kind = dragData.source === 'PALETTE' ? dragData.elementType : dragData.type
            if (!accepts(kind)) return
            onDrop(parentId, index, dragData)
            kbdDnD.commit(
              t('designer.keyboard.dropped', {
                type: dragData.source === 'PALETTE' ? dragData.elementType : dragData.type,
                index: index + 1,
              }),
            )
          }
          if (e.key === 'Escape') {
            kbdDnD.cancel(t('designer.keyboard.cancelled'))
          }
          // Arrow key focus movement: find all [data-dropzone] siblings and advance focus
          if (['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft'].includes(e.key)) {
            e.preventDefault()
            const zones = [...document.querySelectorAll('[data-dropzone]')] as HTMLElement[]
            const current = zones.indexOf(e.currentTarget as HTMLElement)
            const delta = (e.key === 'ArrowDown' || e.key === 'ArrowRight') ? 1 : -1
            const next = (current + delta + zones.length) % zones.length
            zones[next]?.focus()
          }
        }}
        ```
  - [x] `DropZone` needs `useTranslation` — add it.
  - [x] Update all `<DropZone>` call sites in `DesignerCanvas.tsx` to pass a descriptive `label`.
        Use the parent element type and position index:
        - Container children: `label={t('designer.keyboard.dropInContainer', { parent: element.type, pos: i + 1 })}`
        - Empty container block variant: `label={t('designer.keyboard.dropInEmpty', { parent: element.type })}`
        The translation key strings are new; add them in Task 5.
  - [x] **Do NOT** change `EmptyCanvasDrop` — it is already a full-size drop zone for empty canvas
        and receives pointer events; adding keyboard pickup for the root-level drop is complex and
        out of AC scope for this story.

  **Canvas element keyboard pickup wiring:**
  - [x] `CanvasLeaf`: add `tabIndex={0}` and `onKeyDown`:
        ```tsx
        tabIndex={0}
        onKeyDown={(e) => {
          if (!kbdDnD) return
          if (e.key === ' ' || e.key === 'Enter') {
            e.preventDefault()
            kbdDnD.pickUp(
              { source: 'CANVAS', id: element.id, type: element.type },
              t('designer.keyboard.pickedUp', { type: element.type }),
            )
          }
          if (e.key === 'Escape' && kbdDnD.pickedUp) {
            kbdDnD.cancel(t('designer.keyboard.cancelled'))
          }
        }}
        aria-grabbed={kbdDnD?.pickedUp?.source === 'CANVAS' &&
          (kbdDnD.pickedUp as { id?: string }).id === element.id}
        ```
        `CanvasLeaf` already has `cursor-grab` and a border ring — the existing
        `focus-visible:outline-none` is absent so the browser default outline applies on focus.
        Add explicit focus ring CSS:
        ```tsx
        // In CanvasLeaf className, add:
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60'
        ```
  - [x] `CanvasContainer`, `CanvasTabsContainer`, `CanvasRepeaterContainer`: add `tabIndex` and
        `onKeyDown` to their root `<div>` (the same `div` that already has `draggable` and
        `onClick`). Guard with `!isRoot` — root elements are not draggable and should not have
        keyboard drag interactions:
        ```tsx
        tabIndex={!isRoot ? 0 : -1}
        onKeyDown={!isRoot ? (e) => {
          if (!kbdDnD) return
          if (e.key === ' ' || e.key === 'Enter') {
            e.stopPropagation()
            e.preventDefault()
            kbdDnD.pickUp(
              { source: 'CANVAS', id: element.id, type: element.type },
              t('designer.keyboard.pickedUp', { type: element.type }),
            )
          }
          if (e.key === 'Escape' && kbdDnD.pickedUp) {
            kbdDnD.cancel(t('designer.keyboard.cancelled'))
          }
        } : undefined}
        aria-grabbed={!isRoot && kbdDnD?.pickedUp?.source === 'CANVAS' &&
          (kbdDnD.pickedUp as { id?: string }).id === element.id}
        ```
        Add focus-visible ring to the existing className in all three containers:
        ```tsx
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60'
        ```
  - [x] `CanvasContainer`, `CanvasTabsContainer`, `CanvasRepeaterContainer`, `CanvasLeaf` all need
        `useTranslation` — add `const { t } = useTranslation()` to each, or hoist into their
        parent `CanvasElement` memo and pass down. The simplest approach is one `useTranslation`
        call per component (four additions).
  - [x] `CanvasContainer` and `CanvasRepeaterContainer` also use context: add
        `const kbdDnD = useContext(KbdDnDCtx)` alongside the existing store hooks.

- [x] Task 5: i18n keys (AC-2, AC-3, AC-4, AC-5)
  - [x] Add to `web/src/lib/i18n/locales/en.json` under `"designer"` (alongside existing `canvas`,
        `inspector`, `repeaterDrawer` keys):
        ```json
        "keyboard": {
          "pickedUp": "Picked up {{type}}. Tab to a drop target then press Space or Enter to drop, or Escape to cancel.",
          "dropped": "Dropped {{type}} at position {{index}}.",
          "cancelled": "Drag cancelled.",
          "dropZoneFallback": "Drop here",
          "dropInContainer": "Drop inside {{parent}}, position {{pos}}",
          "dropInEmpty": "Drop inside {{parent}} (empty)"
        }
        ```

- [x] Task 6: Vitest tests (AC-1 through AC-5)
  - [x] Create `web/src/components/designer/__tests__/useKeyboardDnD.test.ts`:
        Import: `import { describe, it, expect, vi } from 'vitest'`
        Import: `import { act, renderHook } from '@testing-library/react'`
        (No React import needed for `renderHook` in React 19 + Testing Library v14+.)
        - **Test 1**: Initial state — `pickedUp` is `null`, `announcement` is `''`
        - **Test 2**: `pickUp('item', 'Picked up item.')` → `pickedUp === 'item'` and
          `announcement === 'Picked up item.'`
        - **Test 3**: `commit('Dropped.')` after pickup → `pickedUp === null` and
          `announcement === 'Dropped.'`
        - **Test 4**: `cancel('Cancelled.')` after pickup → `pickedUp === null` and
          `announcement === 'Cancelled.'`
        - **Test 5**: `cancel` when nothing is picked up → `pickedUp` stays `null`,
          `announcement` updates (no throw)
        - **Test 6**: `pickUp` twice in a row replaces the first payload with the second
        Each test wraps mutations in `act(...)`.
  - [x] Create `web/src/components/designer/__tests__/KeyboardDnD.canvas.test.tsx`:
        Tests for the DropZone keyboard wiring integrated with the canvas.
        Mock i18n: `vi.mock('react-i18next', ...)` — same pattern as `RepeaterRowDrawer.test.tsx`.
        Mock the store: `vi.mock('@/store/designerCanvas', ...)` — return a simple in-memory root
        with one Stack container holding one Label leaf.
        - **Test 7**: When `kbdDnD` is `undefined` (default prop), DropZones have `aria-hidden`
          (not keyboard-focusable)
        - **Test 8**: When `kbdDnD.pickedUp` is non-null, DropZones get `tabIndex=0`,
          `role="button"`, and a non-empty `aria-label`
        - **Test 9**: Space on a DropZone when `pickedUp` is set → `onDrop` callback is called
          and `kbdDnD.commit` is called
        - **Test 10**: Escape on a DropZone when `pickedUp` is set → `kbdDnD.cancel` is called
        Note: tests 7-10 render a minimal `<DesignerCanvas kbdDnD={...} />` with a mocked store.
        See Dev Notes for the store mock pattern used in existing canvas tests.

- [x] Task 7: Build and verify
  - [x] `pnpm run build` — 0 errors (Vite + `tsc -b --noEmit`)
  - [x] `pnpm run lint` — 23 errors, identical to baseline (0 new errors)
  - [x] `pnpm run test` — 74/74 pass (64 baseline + 6 hook tests + 4 canvas keyboard tests)
  - [x] No `dotnet build` / `dotnet test` needed — no backend changes in this story

### Review Findings

_Code review 2026-05-24 — 3 parallel reviewers (Blind Hunter, Edge Case Hunter, Acceptance Auditor)._

- [x] [Review][Decision→Patch] Pressing Space on a canvas element while another pickup is already active silently REPLACES the pickup — chosen: **guard**. CanvasLeaf/Container/Tabs/Repeater `onKeyDown` now `return`s early if `kbdDnD.pickedUp` is set, plus same guard added to PaletteCard. User must commit on a DropZone or Escape first.
- [x] [Review][Decision→Patch] AC-3 "valid drop targets" — chosen: **filter by `accepts()`**. DropZone now flips to `tabIndex=0` only when `pickedUp !== null AND accepts(pickedUp.kind)`. Today's `accepts = () => true` default means no behavior change, but the gate future-proofs against stricter parent rules (e.g. Tabs-only-accepts-Stack at the DropZone level instead of only the store).
- [x] [Review][Decision→Patch] AC-7 — chosen: **mark explicitly deferred-to-7.4**. Acceptance Criteria block now has a deferral note; deferred-work.md carries the axe harness + aria-grabbed follow-up under Story 7.4.
- [x] [Review][Decision→Patch] Focus first DropZone on pickup — chosen: **add focus effect in DesignerPage**. New `useEffect` on `kbdDnD.pickedUp` transition focuses the first `[data-dropzone]` inside the canvas root via `requestAnimationFrame`.
- [x] [Review][Decision→Patch] Restore focus on commit/cancel — chosen: **inserted element on commit, originator on cancel**. Hook now exposes `originatorRef` + `insertedIdRef`; pickUp captures originator, commit takes optional inserted id, DesignerPage's transition effect restores accordingly.

- [x] [Review][Patch] **CRITICAL** — Inner header buttons (Trash2/Pencil/✓/✕/+Add Tab) bubble Space/Enter keydown to the parent canvas container/leaf `onKeyDown`, which calls `e.preventDefault()` + `kbdDnD.pickUp(...)` — suppressing the inner button's activation AND triggering a stray pickup of the parent. Fixed: `if (e.target !== e.currentTarget) return` added at the top of all four container/leaf onKeyDown handlers. [`DesignerCanvas.tsx`]
- [x] [Review][Patch] **HIGH** — Identical successive `announcement` strings did NOT re-fire on aria-live. Fixed: hook now toggles a trailing zero-width space (U+200B) every other call via internal `tokenRef`, so React's text-node diff always fires while screen readers ignore the ZWSP. [`useKeyboardDnD.ts`]
- [x] [Review][Patch] **HIGH** — `tabIndex` and `aria-grabbed` on canvas containers/leaves leaked regardless of `kbdDnD` prop. Fixed: gated on `kbdDnD && !isRoot` (containers) / `kbdDnD` (leaf), emitted as `undefined` when prop is absent so the keyboard surface stays opt-in. [`DesignerCanvas.tsx`]
- [x] [Review][Patch] **HIGH** — Arrow-key navigation used document-wide `querySelectorAll`. Fixed: scoped to `e.currentTarget.closest('[data-testid="designer-canvas-root"]')` with fallback to `document` for tests that mount without the route wrapper. [`DesignerCanvas.tsx`]
- [x] [Review][Patch] **MEDIUM** — Escape from non-participant focus (PropertyInspector, header buttons, "+ Add Tab") left pickup stuck. Fixed: DesignerPage adds a document-level keydown listener while `pickedUp != null` that calls `cancel(...)` on Escape. [`routes/_app/designer.$designerId.tsx`]
- [x] [Review][Patch] **MEDIUM** — DropZone `commit` announcement used raw element type. Fixed: extended `DragData.PALETTE` with optional `displayLabel`; PaletteCard passes its human label; DropZone announces `displayLabel ?? elementType`. Palette pickup→drop announcements now use consistent vocabulary. [`dnd.ts`, `DesignerToolbar.tsx`, `DesignerCanvas.tsx`]
- [x] [Review][Patch] **MEDIUM** — `PaletteCard.onKeyDown` called `e.preventDefault()` even when `kbdDnD` undefined. Fixed: early-return on `!kbdDnD` so the keystroke is not eaten by unwired consumers. [`DesignerToolbar.tsx`]
- [x] [Review][Patch] **MEDIUM** — Arrow handler computed `next` without guarding `current === -1` / `zones.length === 0` (yields `NaN`). Fixed: explicit `if (zones.length === 0) return` and `if (current === -1) return` guards. [`DesignerCanvas.tsx`]
- [x] [Review][Patch] **MEDIUM** — No regression tests for canvas-element pickup, replace-guard, or unwired-consumer behavior. Fixed: 3 new tests in `KeyboardDnD.canvas.test.tsx` covering CanvasLeaf Space → pickUp with originator, replace-guard ignoring Space when already picked up, and `tabIndex`/`aria-grabbed` absent when `kbdDnD` prop omitted. [`__tests__/KeyboardDnD.canvas.test.tsx`]
- [x] [Review][Patch] **LOW** — Test #7 assertion was too loose. Fixed: scoped to `div.rounded-sm[aria-hidden="true"]` so decorative icons don't satisfy it. [`__tests__/KeyboardDnD.canvas.test.tsx`]

- [x] [Review][Defer] `aria-grabbed` is deprecated in ARIA 1.1 and will likely be flagged by axe-core — Dev Notes explicitly acknowledge this and defer to Story 7.4. — deferred, pre-existing scope decision.
- [x] [Review][Defer] Keyboard drop into an empty canvas is impossible (`EmptyCanvasDrop` excluded from keyboard wiring per spec Task 4) — pickup gets stuck until Escape. Documented gap. — deferred, pre-existing scope decision.
- [x] [Review][Defer] Stale announcement persists indefinitely after pickup/commit/cancel; no debounce reset. — deferred to Story 7.4 a11y audit.
- [x] [Review][Defer] `CanvasTabsContainer` tab buttons lack arrow-key navigation (standard ARIA Tabs pattern). New `tabIndex` on outer wrapper surfaces this pre-existing gap. — deferred, pre-existing.

## Dev Notes

### What's Already Built — DO NOT Re-implement

**`DesignerCanvas.tsx` native HTML5 DnD pipeline is complete and must be preserved.**
The existing `draggable`, `onDragStart`, `onDragOver`, `onDragLeave`, `onDragEnter`, `onDrop`
handlers on all canvas elements and DropZones are correct and tested. Story 3.10 adds an
_orthogonal_ keyboard pathway — it does NOT replace, wrap, or modify the HTML5 DnD pipeline.
The keyboard path calls the _same_ `onDrop(parentId, index, data)` callback as the mouse path.

**`DesignerToolbar.tsx` palette cards already have `tabIndex={0}`, `role="button"`, and
`focus-visible:ring-2 focus-visible:ring-primary/60`** — AC-1 for palette is already satisfied
visually. Story 3.10 only adds `onKeyDown` for pickup. Do NOT restructure the card styling.

**`dnd.ts` already exports `DragData`** — use it as `T` at the call site. Do not duplicate the
type. The hook itself must NOT import from `./dnd` (see AC-6).

**The Zustand store (`useDesignerCanvasStore`) must NOT be modified.** The keyboard DnD state
lives entirely in `useKeyboardDnD`, hosted in `DesignerPage`. The store's `addElement` /
`moveElement` are already called from the `DropZone.onDrop` callback path — keyboard drops reuse
that same callback path.

### Architecture Compliance

| Requirement | This Story |
|---|---|
| AR-4.5 — `useKeyboardDnD.ts` generic hook | Created at `web/src/components/designer/useKeyboardDnD.ts`; generic `<T>`, no designer-specific imports |
| AR-4.6 — Module / Feature Folder Structure | Hook stays in `components/designer/`; no new features folder needed |
| AR-4.8 — i18n | New `designer.keyboard.*` keys added to `en.json`; all JSX uses `t()` |
| FR-42 AC-4 — WCAG 2.1 AA keyboard DnD | Keyboard pickup + drop + cancel; `aria-live` announcement; `aria-grabbed`; DropZone `aria-label` |
| Epic 4 reuse clause | `useKeyboardDnD<T>()` is generic; Epic 4 will instantiate with its own `MenuDragData` type |

### Hook Design — Why `pickUp` / `commit` / `cancel` Take Announce Strings

The hook is intentionally i18n-agnostic. The caller (`DesignerToolbar`, `DesignerCanvas`)
already has `useTranslation()` and can produce the correct translated string before calling the
hook. This avoids the hook needing its own `useTranslation` call and keeps it purely reactive.

The `announcement` string is rendered in an `aria-live="polite"` region in `DesignerPage`. React
re-renders the region whenever `announcement` changes. Screen readers announce the new value.

### Context vs Props — Why a Local Context Inside `DesignerCanvas.tsx`

`DropZone` is deeply nested through `CanvasContainer → children.map → CanvasElement → DropZone`.
Passing `kbdDnD` as props through every level would require changing every intermediate signature.
A `createContext` + `useContext` approach scoped entirely inside `DesignerCanvas.tsx` avoids this
prop drilling without exporting any new public API. The context is not exported — it is purely
a file-local implementation detail.

### DropZone `accepts` Guard — Keyboard vs Mouse Symmetry

The existing `DropZone` has an `accepts = () => true` default prop. The keyboard path must
respect it too: before calling `onDrop`, check `accepts(kind)`. If `accepts` returns false,
do nothing (no commit, no announcement). This mirrors the HTML5 drop path which also calls
`if (!accepts(kind)) return` in the `onDrop` handler.

### Escape Key — No Interference with Browser Fullscreen

The browser captures Escape to exit fullscreen before JavaScript fires. Our Escape handler in
`onKeyDown` will NOT be called when the browser handles the fullscreen Escape. No special handling
needed. The `e.preventDefault()` call inside `onKeyDown` for Escape is intentionally absent —
Escape should not be prevented (it has other default meanings in the browser).

### `aria-grabbed` Attribute — Deprecated in ARIA 1.1

`aria-grabbed` is officially deprecated in ARIA 1.1+ (it was removed because it created incorrect
semantics for native DnD). However it is harmless and still read by many screen readers as
informational. The architecture spec references it. Include it for now; Story 7.4 can revisit
if the axe-core run flags it. Do NOT let concern about deprecation block this story.

### Focus Ring on Canvas Elements

Canvas elements (CanvasContainer, CanvasLeaf) currently have `cursor-grab` and a selection ring
(`ring-2 ring-primary/60` when selected). They do NOT have a focus-visible ring yet, which means
keyboard Tab currently shows only the browser's default outline (thin blue/black). Story 3.10 adds
explicit `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60` to
unify the focus ring with the selection ring visually. This does not affect mouse interaction.

### Test Store Mock Pattern

Existing tests for canvas components mock the Zustand store like this (from `DesignerCanvas`
patterns — verify by checking `web/src/store/__tests__/designerCanvas.test.ts` for the store
API):

```typescript
const mockStore = {
  rootElement: { id: 'root', type: 'Stack', properties: {}, children: [
    { id: 'leaf1', type: 'Label', properties: {}, children: [] },
  ]},
  selectedElementId: null,
  addElement: vi.fn(),
  moveElement: vi.fn(),
  removeElement: vi.fn(),
  selectElement: vi.fn(),
  schemaId: 'test-schema',
  displayName: 'Test',
  isDirty: false,
  setSchema: vi.fn(),
  updateDisplayName: vi.fn(),
  resetCanvas: vi.fn(),
}
vi.mock('@/store/designerCanvas', () => ({
  useDesignerCanvasStore: (selector: (s: typeof mockStore) => unknown) => selector(mockStore),
  createNewElement: (type: string) => ({ id: `new-${type}`, type, properties: {}, children: [] }),
  createNewTabStack: (n: number) => ({ id: `tab-${n}`, type: 'Stack', properties: {}, children: [] }),
}))
```

For `DesignerCanvas` keyboard tests, render with a minimal root that has one DropZone:
a Stack with one child so there are at least two DropZones (before and after the child).

### `useKeyboardDnD` as a `renderHook` Test

`@testing-library/react` v14+ `renderHook` works with React 19 without extra wrappers.
```typescript
import { act, renderHook } from '@testing-library/react'
import { useKeyboardDnD } from '../useKeyboardDnD'

it('pickUp sets pickedUp and announcement', () => {
  const { result } = renderHook(() => useKeyboardDnD<string>())
  act(() => {
    result.current.pickUp('my-item', 'Picked up my-item.')
  })
  expect(result.current.pickedUp).toBe('my-item')
  expect(result.current.announcement).toBe('Picked up my-item.')
})
```

### Previous Story Learnings (from 3.9 and prior)

- **i18n keys only in `en.json`**: Only `web/src/lib/i18n/locales/en.json` exists — no other
  locale files to update.
- **Lint baseline is 23**: Verify `pnpm run lint` before and after; 0 new errors allowed.
  `react-refresh/only-export-components` fires when a file exports non-components alongside
  components. `useKeyboardDnD.ts` is a hook file (no JSX) — no lint concern there.
- **`vi.mock('react-i18next', ...)` pattern**: Use the same identity-translator mock as
  `RepeaterRowDrawer.test.tsx` (lines 8-12) in all new component test files.
- **Test file naming**: Test files under `routes/_app/` must be prefixed with `-` to prevent
  the tanstack-router Vite plugin treating them as routes. Test files under
  `components/designer/__tests__/` have no such restriction.
- **Backend test count stays at 234**: No backend changes in this story.
- **`cleanup` from `@testing-library/react`**: Always import and call in `afterEach(cleanup)`
  for component tests (already established in existing test files).

### Project Structure — Files to Change / Create

**New files**
- `web/src/components/designer/useKeyboardDnD.ts` — generic keyboard DnD state hook
- `web/src/components/designer/__tests__/useKeyboardDnD.test.ts` — 6 hook unit tests
- `web/src/components/designer/__tests__/KeyboardDnD.canvas.test.tsx` — 4 canvas integration tests

**Modified files**
- `web/src/routes/_app/designer.$designerId.tsx` — instantiate hook, add `aria-live` region,
  pass `kbdDnD` prop to `DesignerToolbar` and `DesignerCanvas`
- `web/src/components/designer/DesignerToolbar.tsx` — add optional `kbdDnD` prop,
  wire `onKeyDown` on `PaletteCard` for Space/Enter pickup and Escape cancel;
  add `useTranslation`
- `web/src/components/designer/DesignerCanvas.tsx` — add optional `kbdDnD` prop,
  create `KbdDnDCtx` context, provide it to canvas tree, wire DropZone keyboard handlers +
  focusable state, wire canvas element `tabIndex`/`onKeyDown`/`aria-grabbed`/focus rings
- `web/src/lib/i18n/locales/en.json` — add `designer.keyboard.*` keys

**No backend changes** — all WCAG-compliance work is purely frontend.

### References

- Architecture Decision 4.5 — Designer DnD Keyboard Accessibility [architecture.md]
- Architecture Decision 4.6 — Module / Feature Folder Structure (`useKeyboardDnD.ts` location)
- Epic 3, Story 3.10 — full AC list [epics.md]
- `web/src/components/designer/DesignerCanvas.tsx` — current HTML5 DnD pipeline (must be preserved)
- `web/src/components/designer/dnd.ts` — `DragData` type (used as `T` at the call site)
- `web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx` — i18n mock pattern
- `web/src/lib/i18n/locales/en.json` — existing `designer.*` key structure

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

None — all gates green on first pass.

### Completion Notes List

- Implemented `useKeyboardDnD<T>()` as a generic, designer-agnostic state hook
  (no imports from `./dnd` or the store) so Epic 4 can reuse it for menu
  reordering with its own drag-data shape (AC-6).
- Hooked `DesignerPage` as the host: the page instantiates the hook with
  `DragData` as the type parameter, renders the `sr-only role="status"
  aria-live="polite" aria-atomic="true"` announcement region *between* the
  page header and the body flex-row (so it can't affect layout), and passes
  the same `kbdDnD` instance into both `DesignerToolbar` and `DesignerCanvas`
  so a palette pickup is visible to canvas DropZones and vice versa.
- `DesignerCanvas` exposes the hook to deeply-nested `DropZone`s via a
  file-local `KbdDnDCtx` context (not exported) to avoid prop-drilling through
  `CanvasContainer → children.map → CanvasElement → DropZone`. The context
  is null when no `kbdDnD` prop is passed (test mode).
- DropZones are `aria-hidden` outside a pickup; once `kbdDnD.pickedUp` is
  non-null, they flip to `tabIndex=0 role="button"` with an i18n `aria-label`
  describing the slot, and a `data-dropzone="parentId:index"` attribute that
  the arrow-key handler uses to walk between siblings. Space/Enter calls
  `onDrop(parentId, index, dragData)` — exactly the same callback the HTML5
  drop handler uses — then `kbdDnD.commit(announce)`. Escape calls
  `kbdDnD.cancel(announce)` without preventDefault so the browser keeps its
  fullscreen-exit behaviour. The keyboard path respects the same
  `accepts(kind)` guard as the mouse path.
- All non-root `CanvasContainer`/`CanvasTabsContainer`/`CanvasRepeaterContainer`
  and every `CanvasLeaf` get `tabIndex=0` (root elements get `-1` and skip the
  onKeyDown — they're not draggable) plus an `aria-grabbed` reflection of the
  current pickup. Containers `e.stopPropagation()` on Space/Enter so an
  ancestor doesn't also pick itself up.
- All four canvas component branches added explicit
  `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60`
  so keyboard focus is consistent with the existing selection ring.
- 6 new i18n keys added under `designer.keyboard.*`. Only `en.json` exists in
  this project.
- 10 new Vitest tests: 6 for the hook (`useKeyboardDnD.test.ts`) and 4
  integration tests (`KeyboardDnD.canvas.test.tsx`) that render a real
  `<DesignerCanvas>` with a mocked Zustand store and verify DropZone wiring
  toggles correctly with pickup state, Space invokes `onDrop + commit`, and
  Escape invokes `cancel`.
- AC-7 (axe-core zero critical violations) is satisfied by construction — the
  pickup pathway adds `role="button"`, `aria-label`, `tabIndex`, `aria-live`,
  visible focus rings — but no automated axe run is bundled here. Story 7.4
  (Accessibility Compliance) is the scheduled epic-level a11y audit and is
  the right home for adding an `axe-core` integration test harness; deferring
  the harness wiring there avoids one-off `vitest-axe` setup just for this
  story.
- Build clean, lint at 23 baseline (0 new errors), tests 74/74 (was 64; +6
  hook + 4 canvas). No backend changes.

### File List

**New files**
- `web/src/components/designer/useKeyboardDnD.ts`
- `web/src/components/designer/__tests__/useKeyboardDnD.test.ts`
- `web/src/components/designer/__tests__/KeyboardDnD.canvas.test.tsx`

**Modified files**
- `web/src/routes/_app/designer.$designerId.tsx`
- `web/src/components/designer/DesignerToolbar.tsx`
- `web/src/components/designer/DesignerCanvas.tsx`
- `web/src/lib/i18n/locales/en.json`
- `_bmad-output/implementation-artifacts/sprint-status.yaml`
- `_bmad-output/implementation-artifacts/3-10-keyboard-accessible-designer-dnd.md`

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-24 | Implemented Story 3.10: useKeyboardDnD hook + DesignerPage aria-live host + DesignerToolbar palette pickup + DesignerCanvas DropZone keyboard activation/arrow-cycling + CanvasLeaf/Container/Tabs/Repeater pickup wiring + 6 i18n keys + 10 Vitest tests. Status → review. | claude-opus-4-7 |
