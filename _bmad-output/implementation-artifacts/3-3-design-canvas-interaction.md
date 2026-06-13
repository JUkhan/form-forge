# Story 3.3: Design Canvas Interaction

Status: done

## Story

As a Platform Admin,
I want to drag components from the palette onto the canvas, reorder them, nest them within structural components, and delete them,
so that I can lay out my form design.

## Acceptance Criteria

1. **AC-1 — Palette Categorization**
   Given I am on the designer canvas
   When the palette renders
   Then all component types are listed, categorized as Structural (Stack, Row, Tabs) and Elements (all others including Repeater, Repeater Field, and all leaf types)

2. **AC-2 — Drop Inserts at Correct Index**
   Given I drag a component from the palette
   When I drop it onto a DropZone
   Then the component is inserted at the correct index within the parent

3. **AC-3 — Same-Parent Reorder**
   Given a component on the canvas
   When I drag it within the same parent
   Then it is reordered to the new position

4. **AC-4 — Leaf Rejects Children**
   Given a Leaf component on the canvas
   When I attempt to drop another component into it
   Then the drop is rejected (Leaf components do not accept children)

5. **AC-5 — Delete Removes Subtree**
   Given a component with children
   When I click its delete icon
   Then the component and all its descendants are removed from the tree

6. **AC-6 — RootElement Updated on Every Change**
   Given any structural change on the canvas
   When the change settles
   Then the canvas emits the updated RootElement JSON (Zustand store `rootElement` is updated reactively and `isDirty` is set to `true`)

## Tasks / Subtasks

- [x] Task 1: Fix toolbar section heading and empty-canvas copy (AC-1)
  - [x] In `web/src/components/designer/DesignerToolbar.tsx` line ~137 — rename the second section heading from `"Interactive Elements"` to `"Elements"` to match AC-1 ("Leaf (all others)")
  - [x] In `web/src/lib/i18n/locales/en.json` — update `designer.canvas.emptyCanvas` value from `"Drop a Stack, Row, or Tabs container here to start building."` to `"Drag a Stack, Row, Tabs, or Repeater container here to begin building."` (Repeater is missing from the existing key; the canvas already accepts Repeater as a root drop)
  - [x] In `web/src/components/designer/DesignerCanvas.tsx` in `EmptyCanvasDrop` — wire the i18n key: import `useTranslation` from `react-i18next` and replace the hardcoded `"Drag a Stack..."` `<p>` text with `t('designer.canvas.emptyCanvas')`

- [x] Task 2: Set up Vitest for frontend tests
  - [x] In `web/vite.config.ts` — add the vitest `test` block. Import `defineConfig` from `'vitest/config'` instead of `'vite'` (they are compatible — `vitest/config` re-exports everything from `'vite'`). Add:
    ```ts
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test-setup.ts'],
    },
    ```
  - [x] Create `web/src/test-setup.ts` — import `@testing-library/jest-dom`:
    ```ts
    import '@testing-library/jest-dom'
    ```
  - [x] In `web/package.json` — add two scripts:
    ```json
    "test": "vitest run",
    "test:watch": "vitest"
    ```
  - [x] In `web/tsconfig.app.json` — add `"vitest/globals"` to the `types` array so TypeScript knows about `describe`, `it`, `expect` etc. The final array should be `["vite/client", "vitest/globals"]`

- [x] Task 3: Write store unit tests (AC-2, AC-3, AC-4, AC-5, AC-6)
  - [x] Create `web/src/store/__tests__/designerCanvas.test.ts`
  - [x] See Dev Notes for full test cases to implement

- [x] Task 4: Build and verify
  - [x] `pnpm run build` — 0 errors
  - [x] `pnpm run lint` — no new errors (pre-existing `react-refresh` warnings are acceptable)
  - [x] `pnpm run test` — all new tests pass

## Dev Notes

### CRITICAL: DnD Implementation is COMPLETE — Do NOT Rewrite

`web/src/components/designer/DesignerCanvas.tsx` (761 lines) and `web/src/store/designerCanvas.ts` (279 lines) were fully implemented in Story 3.1's ESG port and refined by Story 3.1's code review (25 patches). The store has every required action. Do NOT add new actions, do NOT restructure the file, do NOT replace any DnD logic.

Story 3.3's work is: (1) small textual/i18n fixes, (2) set up vitest, (3) write tests that verify the already-implemented behaviors.

---

### 14 vs 15 Component Types — Pre-existing Epics Error

The AC and epics say "14 component types" but the toolbar (`DesignerToolbar.tsx`) has 15:

- **Structural (3):** Stack, Row, Tabs
- **Others (12):** Label, Button, Text Input, TextArea, Number Input, Checkbox, Dropdown, DateTime Picker, Color Picker, Repeater, Repeater Field, Image

Total: 15. The count "14" in the epics is a pre-existing error. **Do NOT remove any type.** The implementation (15 types) is correct. Note this in Completion Notes.

---

### Toolbar Section Heading Rename (Task 1)

`web/src/components/designer/DesignerToolbar.tsx` lines 136-140:

```tsx
// BEFORE (line ~137):
<h2 className="text-xs font-semibold uppercase tracking-wider text-slate-500">
  Interactive Elements
</h2>

// AFTER:
<h2 className="text-xs font-semibold uppercase tracking-wider text-slate-500">
  Elements
</h2>
```

The AC says "Structural (Stack, Row, Tabs) and Leaf (all others)". "Elements" is the neutral label that maps to "Leaf (all others)" without implying incorrect interactivity semantics for types like `Label`.

**Note:** Repeater appears under "Elements" in the toolbar even though it IS a container in the canvas (`CONTAINER_TYPES` includes Repeater). The AC explicitly says "Leaf (all others)" — "all others" means everything that isn't Stack/Row/Tabs, including Repeater. This is the intended categorization for the palette. Do NOT move Repeater to the "Structural" section.

---

### Empty Canvas i18n Fix (Task 1)

The `EmptyCanvasDrop` component in `DesignerCanvas.tsx` currently has a hardcoded string:
```tsx
<p className="text-xs text-slate-500">
  Drag a Stack, Row, Tabs, or Repeater element from the toolbar to begin building your component.
</p>
```

The i18n key `designer.canvas.emptyCanvas` exists in `en.json` but has the wrong value (missing Repeater) and is not wired into the component.

**en.json update:**
```json
"emptyCanvas": "Drag a Stack, Row, Tabs, or Repeater container here to begin building."
```

**DesignerCanvas.tsx EmptyCanvasDrop update:**
```tsx
import { useTranslation } from 'react-i18next'

function EmptyCanvasDrop({ onDrop }: { onDrop: (data: DragData) => void }) {
  const { t } = useTranslation()
  const [state, setState] = useState<'idle' | 'ok'>('idle')
  const enterCountRef = useRef(0)
  // ... existing useEffect for dragend/drop listeners ...
  return (
    <div
      // ... existing drag event handlers ...
    >
      <p className="text-sm font-medium text-slate-700">{t('designer.canvas.emptyCanvasTitle', 'Empty canvas')}</p>
      <p className="text-xs text-slate-500">{t('designer.canvas.emptyCanvas')}</p>
    </div>
  )
}
```

Note: `t('designer.canvas.emptyCanvasTitle', 'Empty canvas')` uses the fallback string pattern — the second argument is the default if the key is missing. You do NOT need to add `emptyCanvasTitle` to `en.json` for this to work (but you may if you prefer consistency).

---

### Vitest Setup Details (Task 2)

**`web/vite.config.ts` — full file after change:**

```ts
import { defineConfig } from 'vitest/config'    // ← changed from 'vite'
import react from '@vitejs/plugin-react'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

export default defineConfig({
  plugins: [
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    strictPort: true,
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_BASE_URL ?? 'http://localhost:5190',
        changeOrigin: true,
        secure: false,
      },
    },
  },
  build: {
    target: 'es2022',
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
    exclude: ['node_modules', 'dist'],
  },
})
```

**`web/src/test-setup.ts`:**
```ts
import '@testing-library/jest-dom'
```

**`web/tsconfig.app.json` — add `"vitest/globals"` to types:**
```json
"types": ["vite/client", "vitest/globals"]
```

**`web/package.json` — add test scripts** (insert after `"lint"` script):
```json
"test": "vitest run",
"test:watch": "vitest"
```

---

### Store Unit Tests — Complete Test Cases (Task 3)

**`web/src/store/__tests__/designerCanvas.test.ts`**

The store is imported directly — no component rendering needed. Tests run before any React component mounts, so `crypto.randomUUID` may need a polyfill. Use `beforeEach(() => useDesignerCanvasStore.setState(INITIAL_STATE))` to reset state between tests.

**Getting INITIAL_STATE:** The store initializes via `INITIAL_STATE` const which is not exported. Reset using:
```ts
import { useDesignerCanvasStore } from '@/store/designerCanvas'

beforeEach(() => {
  useDesignerCanvasStore.setState({
    schemaId: null,
    version: null,
    displayName: 'Untitled Component',
    isDirty: false,
    selectedElementId: null,
    rootElement: null,
  })
})
```

**Test helper:**
```ts
function makeEl(type: string, id = crypto.randomUUID()): DesignerElement {
  return { id, type, properties: {}, children: ['Stack','Row','Tabs','Repeater'].includes(type) ? [] : undefined }
}
```

**Tests to implement:**

```ts
// AC-6: addElement sets isDirty
it('addElement on empty canvas sets rootElement and isDirty', () => {
  const { addElement } = useDesignerCanvasStore.getState()
  const el = makeEl('Stack')
  addElement(null, el, 0)
  const s = useDesignerCanvasStore.getState()
  expect(s.rootElement).not.toBeNull()
  expect(s.isDirty).toBe(true)
  expect(s.selectedElementId).toBe(el.id)
})

// AC-2: drop inserts at correct index
it('addElement inserts at the specified index', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  root.children = []
  store.addElement(null, root, 0)

  const a = makeEl('Label', 'a')
  const b = makeEl('Label', 'b')
  useDesignerCanvasStore.getState().addElement('root', a, 0)
  useDesignerCanvasStore.getState().addElement('root', b, 0)  // insert before a

  const children = useDesignerCanvasStore.getState().rootElement!.children!
  expect(children[0].id).toBe('b')
  expect(children[1].id).toBe('a')
})

// AC-4: leaf rejects children
it('addElement into a leaf type is silently rejected', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const leaf = makeEl('Label', 'leaf')
  useDesignerCanvasStore.getState().addElement('root', leaf, 0)

  const child = makeEl('Button', 'child')
  useDesignerCanvasStore.getState().addElement('leaf', child, 0)

  const leafNode = useDesignerCanvasStore.getState().rootElement!.children![0]
  expect(leafNode.children).toBeUndefined()
})

// Tabs child invariant: only Stacks allowed
it('addElement into Tabs rejects non-Stack children', () => {
  const { addElement } = useDesignerCanvasStore.getState()
  const tabs = makeEl('Tabs', 'tabs')
  addElement(null, tabs, 0)
  const label = makeEl('Label', 'lbl')
  useDesignerCanvasStore.getState().addElement('tabs', label, 0)

  // Label should NOT be added to Tabs children
  const tabsNode = useDesignerCanvasStore.getState().rootElement!
  // Tabs ships with 1 default tab Stack from createNewElement('Tabs'), but here
  // we manually set a bare Tabs element — verify rejection
  const count = tabsNode.children?.length ?? 0
  const hasLabel = tabsNode.children?.some(c => c.id === 'lbl') ?? false
  expect(hasLabel).toBe(false)
})

// AC-5: delete removes entire subtree
it('removeElement removes the element and all its children', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const parent = makeEl('Row', 'parent')
  useDesignerCanvasStore.getState().addElement('root', parent, 0)
  const child = makeEl('Label', 'child')
  useDesignerCanvasStore.getState().addElement('parent', child, 0)

  useDesignerCanvasStore.getState().removeElement('parent')

  const rootChildren = useDesignerCanvasStore.getState().rootElement!.children!
  expect(rootChildren).toHaveLength(0)
})

// AC-5: deleting root clears canvas
it('removeElement on root element nullifies rootElement', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  useDesignerCanvasStore.getState().removeElement('root')

  expect(useDesignerCanvasStore.getState().rootElement).toBeNull()
})

// AC-5: selection cleared when selected element is deleted
it('removeElement clears selectedElementId when it matches the deleted element', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const child = makeEl('Label', 'c')
  useDesignerCanvasStore.getState().addElement('root', child, 0)
  useDesignerCanvasStore.setState({ selectedElementId: 'c' })

  useDesignerCanvasStore.getState().removeElement('c')

  expect(useDesignerCanvasStore.getState().selectedElementId).toBeNull()
})

// AC-3: same-parent reorder (off-by-one fix)
it('moveElement reorders within the same parent correctly', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const a = makeEl('Label', 'a')
  const b = makeEl('Label', 'b')
  const c = makeEl('Label', 'c')
  useDesignerCanvasStore.getState().addElement('root', a, 0)
  useDesignerCanvasStore.getState().addElement('root', b, 1)
  useDesignerCanvasStore.getState().addElement('root', c, 2)
  // Order: a, b, c — move 'a' to after 'c'
  useDesignerCanvasStore.getState().moveElement('a', 'root', 3)

  const children = useDesignerCanvasStore.getState().rootElement!.children!
  expect(children.map(ch => ch.id)).toEqual(['b', 'c', 'a'])
})

// moveElement: cannot drop into self
it('moveElement ignores request when id === targetParentId', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const box = makeEl('Row', 'box')
  useDesignerCanvasStore.getState().addElement('root', box, 0)
  const before = JSON.stringify(useDesignerCanvasStore.getState().rootElement)
  useDesignerCanvasStore.getState().moveElement('box', 'box', 0)
  const after = JSON.stringify(useDesignerCanvasStore.getState().rootElement)
  expect(after).toBe(before)
})

// moveElement: cannot drop into own descendant
it('moveElement ignores request when target is inside the moved subtree', () => {
  const store = useDesignerCanvasStore.getState()
  const root = makeEl('Stack', 'root')
  store.addElement(null, root, 0)
  const outer = makeEl('Row', 'outer')
  useDesignerCanvasStore.getState().addElement('root', outer, 0)
  const inner = makeEl('Stack', 'inner')
  useDesignerCanvasStore.getState().addElement('outer', inner, 0)
  const before = JSON.stringify(useDesignerCanvasStore.getState().rootElement)
  useDesignerCanvasStore.getState().moveElement('outer', 'inner', 0)
  const after = JSON.stringify(useDesignerCanvasStore.getState().rootElement)
  expect(after).toBe(before)
})

// AC-6: setSchema resets isDirty
it('setSchema loads schema and resets isDirty and selectedElementId', () => {
  const store = useDesignerCanvasStore.getState()
  useDesignerCanvasStore.setState({ isDirty: true, selectedElementId: 'x' })
  store.setSchema({
    designerId: 'form1',
    displayName: 'Form 1',
    status: 'Draft',
    latestVersion: 1,
    rootElement: null,
    createdAt: new Date().toISOString(),
    updatedAt: null,
  })
  const s = useDesignerCanvasStore.getState()
  expect(s.isDirty).toBe(false)
  expect(s.selectedElementId).toBeNull()
  expect(s.schemaId).toBe('form1')
  expect(s.version).toBe(1)
})
```

---

### Why Native HTML5 DnD Events Can't Be Tested in jsdom

`dragstart`, `dragover`, `dragenter`, `dragleave`, `drop` are **not dispatched by jsdom** (the vitest default environment). `dataTransfer.setData` / `getData` are no-ops in jsdom. Therefore:

- Do NOT write component-level DnD tests with `fireEvent.dragStart` etc. — they will silently succeed without exercising any real logic.
- The store unit tests (Task 3) are the correct approach: they call store actions directly, which is the same code path the drop handlers invoke.
- Full E2E DnD testing with Playwright is deferred to Story 3.10 (keyboard accessibility) which will add playwright to the project.

---

### AC-6 Implementation Note

"Canvas emits the updated RootElement JSON" is satisfied by the Zustand store subscription model:

- Every `addElement`, `removeElement`, `moveElement` call in the store sets `isDirty: true` and updates `rootElement`.
- The designer page (`designer.$designerId.tsx`) reads `rootElement` via `useDesignerCanvasStore((s) => s.rootElement)` — React re-renders the component when `rootElement` changes.
- The Save button payload uses the live `rootElement` from the store.

No additional "emit" callback or event bus is needed.

---

### Previous Story Learnings

From Story 3.2 (deferred items that are still pre-existing in this story):
- `saveVersion` PUT call returns 404 (Story 3.6 implements the endpoint) — DO NOT fix this in 3.3. The "Could not save" error toast is expected behavior.
- `version: 0` → 1 coercion in `designer.$designerId.tsx` line 118 is intentional — do not remove.
- `EmptyCanvasDrop` rendering with `rootElement: null` is the correct empty-state behavior.

From Story 3.1 code review:
- The same-parent reorder off-by-one fix is already in `DesignerCanvas.tsx` `handleDrop` at lines 287-295. Do NOT simplify or remove it.
- `DropZone` uses `enterCountRef` to avoid ok→idle→ok flicker on child boundaries — this is deliberate. Do NOT remove it.
- `pointer-events: none` on `ElementRenderer` inside `CanvasLeaf` is intentional (prevents `disabled` form controls from absorbing click events). Do NOT remove it.

---

### Files to Change

**Modified files:**
- `web/src/components/designer/DesignerToolbar.tsx` — rename section heading (1 line)
- `web/src/components/designer/DesignerCanvas.tsx` — wire i18n in EmptyCanvasDrop (~5 lines)
- `web/src/lib/i18n/locales/en.json` — update `emptyCanvas` key value
- `web/vite.config.ts` — add test config block + change import
- `web/package.json` — add `test` and `test:watch` scripts
- `web/tsconfig.app.json` — add `vitest/globals` to types

**New files:**
- `web/src/test-setup.ts`
- `web/src/store/__tests__/designerCanvas.test.ts`

**Files NOT to touch:**
- `web/src/components/designer/DesignerCanvas.tsx` — DnD logic (only the EmptyCanvasDrop i18n fix)
- `web/src/store/designerCanvas.ts` — any action logic
- `web/src/components/designer/dnd.ts`
- `web/src/components/designer/ElementRenderer.tsx`
- `web/src/components/designer/DynamicComponent.tsx`
- `web/src/components/designer/PropertyInspector.tsx`
- `web/src/routes/_app/designer.$designerId.tsx`
- `web/routeTree.gen.ts` (auto-generated)
- All Epic 2 files
- All backend files

### Review Findings

Source: `bmad-code-review` parallel pass (Blind Hunter + Edge Case Hunter + Acceptance Auditor), 2026-05-23. Acceptance Auditor reports zero AC violations.

- [x] [Review][Decision→Patch] vitest globals plumbing removed — Decision resolved 2026-05-23: dropped `globals: true` from `web/vite.config.ts` and `vitest/globals` from `web/tsconfig.app.json:7`. Tests already use explicit `vitest` imports so nothing breaks; eliminates the typing-leak risk into production source. Follow-up: `web/src/test-setup.ts` now uses the explicit `expect.extend(matchers)` pattern instead of `@testing-library/jest-dom`'s side-effect import, which required a global `expect`.

- [x] [Review][Patch] Use `toEqual` (with `structuredClone` snapshot) instead of `JSON.stringify` equality in moveElement reject tests [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Added cross-parent moveElement test — AC-3's primary user action now covered [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Added boundary-index addElement test — exercises clamp guards at `designerCanvas.ts:112,176,252` [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Strengthened `setSchema` test to assert `displayName` and `rootElement` are applied [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Switched `beforeEach` to call `resetCanvas()` — future store fields can't leak across tests [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Added Stack-leaves-Tabs strip-tab-props test — guards `designerCanvas.ts:243-246` regression [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Tabs invariant tests now use `createNewElement('Tabs')` so they exercise the production-realistic shape (default tab Stack child preserved) [web/src/store/__tests__/designerCanvas.test.ts]
- [x] [Review][Patch] Use `[...configDefaults.exclude]` so Vitest defaults (`.git`, `.cache`, `.idea`, coverage outputs) aren't shadowed [web/vite.config.ts]
- [x] [Review][Patch] Strengthened AC-4 leaf-rejection test with deep-search invariants (child id nowhere in tree, child count unchanged, `isDirty` unchanged) [web/src/store/__tests__/designerCanvas.test.ts]

- [x] [Review][Defer] Toolbar headings ("Structural Elements", "Elements") not internationalized [web/src/components/designer/DesignerToolbar.tsx:123,139] — deferred, pre-existing pattern; story Task 1 scope was rename only
- [x] [Review][Defer] moveElement guards on root id and missing source id untested [web/src/store/designerCanvas.ts:219,237] — deferred, edge guards
- [x] [Review][Defer] moveElement with `targetParentId: null` and a leaf root untested [web/src/store/designerCanvas.ts:250] — deferred, edge guard
- [x] [Review][Defer] `updateElementProperty` / `updateDisplayName` store actions untested [web/src/store/designerCanvas.ts:153-161] — deferred to Story 3.4 (Configure Component Properties)
- [x] [Review][Defer] tanstackRouter codegen race risk under vitest workers [web/vite.config.ts] — deferred, speculative; tests pass today
- [x] [Review][Defer] No `afterEach(cleanup)` from `@testing-library/react` [web/src/test-setup.ts] — deferred, only matters when component tests land
- [x] [Review][Defer] `useTranslation` Suspense / i18next init race [web/src/components/designer/DesignerCanvas.tsx:89,141-142] — deferred, app-wide pattern not introduced here
- [x] [Review][Defer] No coverage config or CI test integration [web/vite.config.ts, web/package.json] — deferred to a testing-infra story
- [x] [Review][Defer] `addElement` with duplicate id behavior is undefined [web/src/store/designerCanvas.ts:163] — deferred, caller-error edge case
- [x] [Review][Defer] `makeEl` test helper assumes `crypto.randomUUID()` available [web/src/store/__tests__/designerCanvas.test.ts:9] — deferred, jsdom 29 has it; mirror store's fallback when convenient

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — `claude-opus-4-7[1m]`

### Completion Notes List

- **Task 1 — Toolbar heading & empty-canvas i18n (AC-1):** Renamed `DesignerToolbar.tsx` section heading `Interactive Elements` → `Elements`. Wired `useTranslation()` into `EmptyCanvasDrop` and updated `designer.canvas.emptyCanvas` value in `en.json` to include Repeater. Added `designer.canvas.emptyCanvasTitle` key (`"Empty canvas"`) so the title is also localized rather than hardcoded.
- **Task 2 — Vitest setup:** `vite.config.ts` now imports `defineConfig` from `'vitest/config'` and adds a `test` block (`jsdom` env, globals, `./src/test-setup.ts`). Created `src/test-setup.ts` importing `@testing-library/jest-dom`. Added `test` / `test:watch` scripts to `package.json`. Added `vitest/globals` to `tsconfig.app.json` types array.
- **Task 2 deviation — pool: 'threads':** On Windows + vitest 4.1.7 the default `forks` pool intermittently fails worker startup with `Timeout waiting for worker to respond` (`[vitest-pool-runner]`). Switched to `pool: 'threads'` in the test config — 13/13 tests now run reliably in ~1s. Documented inline with a comment.
- **Task 3 — Store tests:** 13 tests in `src/store/__tests__/designerCanvas.test.ts` covering all of AC-2/3/4/5/6 plus extras (Tabs-accepts-Stack, selection cleared when removed element is inside a deleted subtree). All tests reset state via `useDesignerCanvasStore.setState(INITIAL_STATE)` in `beforeEach`. Explicit `vitest` imports used in addition to globals so the file works regardless of `globals` setting.
- **Task 4 — Build/lint/test:**
  - `pnpm run build` — clean (vite build + `tsc -b --noEmit`).
  - `pnpm run lint` — 26 errors, ALL pre-existing `react-refresh/only-export-components` in route files I did NOT touch (verified via `git diff --name-only HEAD`). Per Dev Notes these are acceptable.
  - `pnpm run test` — 13/13 pass.
- **Per Dev Notes — 15 vs 14 component types:** The epic-level AC text says "14 component types" but the toolbar has 15 (3 Structural + 12 Elements). 15 is the intended count; the "14" in the epics is a pre-existing documentation error. Not fixed in this story.
- **Per Dev Notes — Native HTML5 DnD events:** Not testable in jsdom (`dragstart`/`dragover`/`drop` aren't dispatched; `dataTransfer.setData`/`getData` are no-ops). Component-level DnD tests would silently succeed without exercising real logic — store unit tests are the correct approach. Full E2E DnD coverage is deferred to Story 3.10 (Playwright).
- **AC-6 satisfied by Zustand subscription model:** Every `addElement`/`removeElement`/`moveElement` sets `isDirty: true` and updates `rootElement`; React re-renders subscribers automatically. No additional emit/event bus required (confirmed by `setSchema` test and isDirty assertion in `addElement` test).

### File List

**Modified:**
- `web/src/components/designer/DesignerToolbar.tsx` — section heading rename
- `web/src/components/designer/DesignerCanvas.tsx` — `useTranslation` import + EmptyCanvasDrop i18n wiring
- `web/src/lib/i18n/locales/en.json` — `emptyCanvas` value updated, `emptyCanvasTitle` added
- `web/vite.config.ts` — `vitest/config` import + `test` block (with `pool: 'threads'` for Windows)
- `web/package.json` — `test` and `test:watch` scripts
- `web/tsconfig.app.json` — `vitest/globals` in `types`
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story 3.3 status: ready-for-dev → in-progress → review

**New:**
- `web/src/test-setup.ts`
- `web/src/store/__tests__/designerCanvas.test.ts`

### Change Log

| Date | Author | Change |
|---|---|---|
| 2026-05-23 | bmad-create-story | Story created — comprehensive context for Story 3.3 canvas interaction tests + small fixes |
| 2026-05-23 | bmad-dev-story (Opus 4.7) | Story 3.3 implemented — Task 1 toolbar/i18n fixes, Task 2 Vitest setup (with `pool: 'threads'` for Windows compatibility), Task 3 13 store unit tests covering AC-2/3/4/5/6, Task 4 build+lint+test all green |
