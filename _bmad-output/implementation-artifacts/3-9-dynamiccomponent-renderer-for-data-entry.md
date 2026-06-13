# Story 3.9: DynamicComponent Renderer for Data Entry

Status: done (post re-review patches 2026-05-24)

## Story

As the system,
I render any bound Designer version as a live, submittable form for end-users,
so that data entry requires no per-module custom code.

## Acceptance Criteria

**AC-1 — Schema fetch via DynamicComponent**
Given an authenticated user opens a record entry view
When the page needs the schema
Then `DynamicComponent` fetches the RootElement from `GET /api/designers/{designerId}/versions/{version}` (or latest) via TanStack Query with `staleTime: 300_000`

**AC-2 — All 14 component types render**
Given the fetched RootElement
When `DynamicComponent` renders it
Then all 14 component types render as appropriate input controls
*(Already true in ElementRenderer — no new work. AC verifies the route correctly mounts the component.)*

**AC-3 — Visibility conditions respected**
Given the form has visibility conditions
When the user changes input values
Then subtrees correctly show or hide based on `computeVisibility` against current form values
*(Already true — AC verifies the full integration in the data entry route.)*

**AC-4 — Repeater row editing wired**
Given a Repeater element with `rowDesignerId` set
When the user clicks Add Row or the Edit pencil
Then `RepeaterRowDrawer` opens with `DynamicComponent` rendering the row schema (not the TODO placeholder)
And the Save button in the drawer footer commits the row via `onSave`
And Escape / backdrop click closes the drawer without saving

**AC-5 — External Save button via submitRef**
Given the data entry route is open
When the user clicks the external Save button
Then `submitRef.current()` is invoked
And `onSave(payload)` is called with collected form values, excluding hidden fields and undefined values

## Tasks / Subtasks

- [x] Task 1: Extract `RepeaterRowDrawer` to a separate file (AC-4)
  - [x] Create `web/src/components/designer/RepeaterRowDrawer.tsx` — move the `RepeaterRowDrawer` function (and only it) out of `ElementRenderer.tsx`
  - [x] The component is currently at the bottom of `ElementRenderer.tsx` (~lines 1845–1929) as a named `function RepeaterRowDrawer(...)`. Move it wholesale.
  - [x] Add `import DynamicComponent from './DynamicComponent'` at the top of the new file (this is the reason for extraction — avoids circular import since `DynamicComponent.tsx` → `ElementRenderer.tsx` → `DynamicComponent.tsx` would be circular)
  - [x] Import: `import { useCallback, useEffect, useRef, useState } from 'react'`, `import { X } from 'lucide-react'`, `import { cn } from '@/lib/utils'`
  - [x] In `ElementRenderer.tsx`: remove the `RepeaterRowDrawer` function body and add `import RepeaterRowDrawer from './RepeaterRowDrawer'` at the import block top (also dropped `X` from the lucide-react import since it was only used by the extracted drawer)

- [x] Task 2: Wire DynamicComponent inside RepeaterRowDrawer (AC-4)
  - [x] The existing component already has `initialData` and `onSave` in the TypeScript props interface but they are NOT destructured in the function body — add them to the destructuring: `{ designerId, version, mode, initialData, onSave, onClose }`
  - [x] Add local state and refs inside the component:
    ```tsx
    const submitRef = useRef<(() => void) | null>(null)
    const [isReady, setIsReady] = useState(false)
    const [isValid, setIsValid] = useState(false)
    ```
  - [x] Replace the TODO placeholder `<div>` (the `flex-1 overflow-y-auto px-5 py-4` block with the dashed-border div) with:
    ```tsx
    <div className="flex-1 overflow-y-auto px-5 py-4">
      <DynamicComponent
        designerId={designerId}
        version={version}
        initialData={initialData}
        onSave={(data) => { onSave(data); onClose() }}
        onValidityChange={setIsValid}
        onReadyChange={setIsReady}
        submitRef={submitRef}
      />
    </div>
    ```
  - [x] Add a footer below the scroll area with Save and Cancel buttons:
    ```tsx
    <div className="flex shrink-0 justify-end gap-2 border-t border-slate-200 px-5 py-3">
      <button
        type="button"
        onClick={handleClose}
        className="rounded px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-100"
      >
        Cancel
      </button>
      <button
        type="button"
        onClick={() => submitRef.current?.()}
        disabled={!isReady || !isValid}
        className="rounded bg-slate-800 px-3 py-1.5 text-sm text-white hover:bg-slate-700 disabled:opacity-50"
      >
        Save
      </button>
    </div>
    ```
  - [x] The drawer shell, close-on-Escape, backdrop click, and slide animation already work — do NOT rewrite them
  - [x] When `rowDesignerId === ''` (no designer binding configured), show a friendly message instead of mounting DynamicComponent:
    ```tsx
    {designerId === '' ? (
      <div className="rounded border border-dashed border-slate-300 p-4 text-xs text-slate-400">
        No row designer configured for this Repeater.
      </div>
    ) : (
      <DynamicComponent ... />
    )}
    ```
    And disable the Save button when `designerId === ''`.

- [x] Task 3: Create data entry route (AC-1, AC-2, AC-3, AC-5)
  - [x] Create `web/src/routes/_app/data.$designerId.tsx` — TanStack Router file-based route at `/_app/data/$designerId`. The route file is a thin wrapper: it pulls `designerId` via `Route.useParams()` and forwards to `<DataEntryPage designerId={...} />`. `DataEntryPage` itself lives in `web/src/components/dataEntry/DataEntryPage.tsx` (extracted so the tanstack-router-plugin's autoCodeSplitting transform does not wrap `Route.component` with `lazyRouteComponent` for the test render path, and so non-Route exports do not break code-splitting per the plugin's warning).
  - [x] Route definition pattern (follow `designer.$designerId.tsx` for the exact `createFileRoute` shape used in this repo):
    ```tsx
    export const Route = createFileRoute('/_app/data/$designerId')({
      component: DataEntryPage,
    })
    ```
  - [x] `DataEntryPage` component:
    - `const { designerId } = Route.useParams()`
    - `const submitRef = useRef<(() => void) | null>(null)`
    - `const [isReady, setIsReady] = useState(false)`
    - `const [isValid, setIsValid] = useState(false)`
    - `const [isSaving, setIsSaving] = useState(false)`
  - [x] onSave stub (Epic 6 will replace with real mutation):
    ```tsx
    // Payload parameter is deliberately omitted (TS bivariance allows
    // assigning a () => void into DC's (p: X) => void slot) so the stub
    // CANNOT accidentally leak record contents into Sentry/Datadog console
    // breadcrumbs. Epic 6 reinstates the payload parameter when wiring the
    // real POST /api/data/{designerId}/records mutation.
    const handleSave = useCallback(() => {
      setIsSaving(true)
      toast.success(t('data.entry.saveStub'))
      setIsSaving(false)
    }, [t])
    ```
  - [x] Page layout:
    ```tsx
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 items-center justify-between border-b border-slate-200 px-6 py-4">
        <h1 className="text-base font-semibold text-slate-800">{t('data.entry.newRecord')}</h1>
        <button
          type="button"
          onClick={() => submitRef.current?.()}
          disabled={!isReady || !isValid || isSaving}
          className="rounded bg-slate-800 px-4 py-1.5 text-sm text-white hover:bg-slate-700 disabled:opacity-50"
        >
          {isSaving ? t('data.entry.saving') : t('data.entry.save')}
        </button>
      </div>
      <div className="flex-1 overflow-y-auto p-6">
        <DynamicComponent
          designerId={designerId}
          onSave={handleSave}
          onValidityChange={setIsValid}
          onReadyChange={setIsReady}
          submitRef={submitRef}
        />
      </div>
    </div>
    ```
  - [x] Imports needed: `createFileRoute` from `@tanstack/react-router`, `useCallback`, `useRef`, `useState`, `DynamicComponent`, `toast` from `sonner`, `useTranslation` from `react-i18next`

- [x] Task 4: i18n keys (AC-5)
  - [x] Add to `web/src/lib/i18n/locales/en.json` under a new `"data"` top-level key:
    ```json
    "data": {
      "entry": {
        "newRecord": "New Record",
        "save": "Save",
        "saving": "Saving…",
        "saveStub": "Record submitted (data persistence coming in Epic 6)."
      }
    }
    ```

- [x] Task 5: Vitest tests
  - [x] Create `web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx`:
    - Mock `DynamicComponent` (avoids real fetch inside unit test)
    - **Test 1**: Renders DynamicComponent when `designerId` is non-empty; drawer body contains the mock component output
    - **Test 2**: Renders "No row designer configured" message and Save button is disabled when `designerId` is `''`
    - **Test 3**: Save button is disabled when `isReady=false` (DynamicComponent not yet ready)
    - **Test 4**: Save button is disabled when `isValid=false` (form has validation errors)
    - **Test 5**: Clicking Save calls `submitRef.current()` when ready and valid
    - **Test 6**: Escape key triggers close (Escape keydown event on window — asserts the closing-animation class flips on the inner panel)
  - [x] Create `web/src/routes/_app/-data.$designerId.test.tsx` (dash prefix per convention):
    - **Test 7**: Route renders DynamicComponent with correct `designerId` from params
    - **Test 8**: Save button is disabled until DynamicComponent fires `onReadyChange(true)` and `onValidityChange(true)`

- [x] Task 6: Build and verify
  - [x] `pnpm run build` — 0 errors (Vite + `tsc -b --noEmit`); new code-split chunk `data._designerId-*.js` (1.18 kB)
  - [x] `pnpm run lint` — 23 errors, identical to the pre-existing baseline (verified by stashing the diff and re-running). 0 new errors introduced. The story spec's quoted baseline of "27 pre-existing" was higher than the actual repo state at branch-tip (23); confirmed empirically before/after.
  - [x] `pnpm run test` — 63/63 pass (53 baseline + 7 RepeaterRowDrawer + 3 DataEntryPage). Post-patch (review #1 patches) counts; the original 8 tests in this Task 6 specification grew to 10 after the patches added `initialData propagation` (drawer) and `Save-click invokes submitRef → toast` (DataEntryPage). A subsequent re-review (2026-05-24, patches commit `7989fd1`) added a drag-out regression test, bringing the drawer file to 8 tests for a current grand total that the re-review re-verified.
  - [x] No `dotnet build` / `dotnet test` needed — no backend changes in this story

## Dev Notes

### What's Already Built — Do NOT Re-implement

**DynamicComponent is feature-complete for this story's ACs.** The entire schema-fetch (AC-1), 14-type render (AC-2), visibility engine (AC-3), and submitRef/onSave flow (AC-5) are already implemented in `web/src/components/designer/DynamicComponent.tsx`. Story 3.9's job is to:
1. Wire the `RepeaterRowDrawer` stub (AC-4)
2. Create the data entry route that hosts DynamicComponent

**Do NOT modify DynamicComponent.tsx.** Its internal staleTime (300_000), query key convention (`['component-schema', designerId, version ?? 'latest']`), payload building, and submitRef wiring are correct and tested. Touch it only if a bug is discovered.

**ElementRenderer.tsx existing behavior must be preserved.** The Repeater rendering (RepeaterInteractive, RepeaterPreview), all 14 leaf renderers, CSS parsing, image presigning, and DynamicDropdown are all correct. The only change to ElementRenderer is removing the `RepeaterRowDrawer` function body and adding its import.

### Task 1 — Circular Import Problem

`DynamicComponent.tsx` imports `ElementRenderer.tsx` (at the top: `import ElementRenderer from './ElementRenderer'`). If `ElementRenderer.tsx` also imports `DynamicComponent.tsx`, you get a circular module dependency. While modern bundlers (Vite/webpack) handle circular imports at call-time (not init-time), the resulting behavior is non-deterministic if either module-level expression is evaluated before the other resolves.

The safe fix is extraction: move `RepeaterRowDrawer` (a function component, not a module-level expression) into its own file and import DynamicComponent there. The circular chain breaks.

**`RepeaterRowDrawer` is self-contained** — it only uses `useState`, `useCallback`, `useEffect`, `useRef`, `X` from `lucide-react`, `cn`, and the two things being added: `DynamicComponent` and component-local state. Nothing from ElementRenderer's internal scope is referenced inside it.

### Task 2 — RepeaterRowDrawer Existing Structure

The component already has:
- `closing` state + CSS `translate-x-full` → `translate-x-0` slide animation
- `handleClose()` that sets `closing=true`; `onClose()` fires via `transitionEnd` OR 350ms safety timer
- Escape key listener via `window.addEventListener('keydown', ...)`
- Backdrop div that calls `handleClose` on click; inner panel that calls `e.stopPropagation()`

Do not rewrite these. Only:
1. Add `initialData` and `onSave` to destructuring
2. Add `submitRef`, `isReady`, `isValid` local state/refs
3. Replace the dashed-border placeholder `<div>` with DynamicComponent (or the empty-designerId fallback)
4. Add the footer Save/Cancel bar

**Critical**: `onSave` in RepeaterRowDrawer should call `onClose()` after calling the outer `onSave(data)` so the drawer animates closed after a row commit. The `DynamicComponent`'s internal `onSave` prop is set to `(data) => { onSave(data); onClose() }`.

### Task 3 — Route Registration

TanStack Router uses file-based routing — just creating `data.$designerId.tsx` inside `_app/` auto-registers the route. The `routeTree.gen.ts` file is regenerated by the Vite plugin on next dev server start or build. No manual route registration needed.

**Check `designer.$designerId.tsx`** for the exact `createFileRoute` call shape used in this repo (the path string format and any `validateSearch`/`loaderDeps` patterns), then mirror it.

**Route path**: `'/_app/data/$designerId'` — the `_app` prefix means it lives inside the authenticated layout (`_app.tsx`). Authentication is already enforced by the layout.

### Task 4 — onSave Stub Rationale

Epic 6 (Stories 6.1–6.11) owns the backend CRUD API (`POST /api/data/{designerId}/records`). Story 3.9's `handleSave` is an intentional stub — `console.log` + `toast.success`. When Epic 6 lands, the dev replaces `handleSave` with a TanStack `useMutation` that calls a new `dataEntryApi.createRecord(designerId, payload)`. No architectural harm is caused by the stub; the route and DynamicComponent wiring are production-ready.

### Architecture Compliance

| Requirement | This Story |
|---|---|
| AR-4.10 DynamicComponent Integration | Data entry route uses external `submitRef`; `onSave` callback hands payload to handler; no shared state with react-hook-form |
| AR-4.6 Module / Feature Folder Structure | Route at `routes/_app/data.$designerId.tsx`; `RepeaterRowDrawer.tsx` stays in `components/designer/` |
| AR-4.3 Error Boundary & Loading Strategy | DynamicComponent renders its own pulse skeleton on load + fallbackUI on error — no extra loading state needed in the route |
| AR-4.7 HTTP Client | No direct fetch — DynamicComponent uses `httpClient` via `designerApi.getSchema` internally |
| AR-4.8 i18n | New `data.entry.*` keys added to `en.json`; `useTranslation()` in route component |
| AR-4.4 Toast | `toast.success` from `sonner` for save stub confirmation |
| TanStack Query key convention | DynamicComponent key is `['component-schema', designerId, version ?? 'latest']` — already correct, do not duplicate |
| FR-41 Loading/error/empty states | `isReady` gates the Save button; DynamicComponent handles its own loading skeleton |

### Project Structure — Files to Change / Create

**New files**
- `web/src/components/designer/RepeaterRowDrawer.tsx` — extracted from ElementRenderer, wired to DynamicComponent
- `web/src/routes/_app/data.$designerId.tsx` — data entry route
- `web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx` — 6 unit tests
- `web/src/routes/_app/-data.$designerId.test.tsx` — 2 integration tests

**Modified files**
- `web/src/components/designer/ElementRenderer.tsx` — remove `RepeaterRowDrawer` function body, add import
- `web/src/lib/i18n/locales/en.json` — add `"data": { "entry": { ... } }` block

**No backend changes** — `GET /api/designers/{designerId}/versions/{version}` already exists (Story 3.6). No new endpoints needed.

### Previous Story Learnings (from 3.8)

- **Pre-existing lint warnings**: 27 warnings total (24 `react-refresh/only-export-components` + 3 other). New exports in `RepeaterRowDrawer.tsx` (default export only) and `data.$designerId.tsx` (Route + default component) will not add new warnings since each is in its own file with default export only.
- **Dash-prefixed test files**: test files in `_app/` must be prefixed with `-` (e.g., `-data.$designerId.test.tsx`) so TanStack Router's Vite plugin doesn't treat them as routes. The `components/designer/__tests__/` folder has no such restriction.
- **`toast` from `sonner`** — already installed and available. Import as `import { toast } from 'sonner'` (no default export in sonner v2+). Check existing usage in `designer.library.tsx` for the exact import.
- **`useNavigate` pattern**: not needed here — the route is a terminal form page with no navigation on save (stub just toasts).
- **Test QueryClient**: use a single `makeQC()` instance per test, thread it to both the `QueryClientProvider` wrapper and any explicit `qc=` prop. Do not allocate two instances in the same test.
- **Backend test count is 234** — no changes, count stays the same.

### Repeater rowDesignerId Property Convention

The Repeater element schema property `p.rowDesignerId` is a string (set in PropertyInspector) that identifies the designer whose schema is used for each row's editing form. `p.rowVersion` is a number (or undefined for latest). These come from the element's `properties` bag and are already read in ElementRenderer at lines 1285–1286:
```typescript
const rowDesignerId = String(p.rowDesignerId ?? '').trim()
const rowVersion = toRowVersion(p.rowVersion)
```
Both are already threaded into the `RepeaterRowDrawer` call site at lines 1831–1832. No changes needed to the call site.

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- Initial route test render under the tanstack-router-plugin's `autoCodeSplitting: true` transform caused a "component suspended inside act" warning and an empty DOM: the plugin wraps `Route.component` in `lazyRouteComponent(...)` at transform time, and rendering that without a router `<Outlet />` / Suspense boundary suspended the whole tree. The plugin also emits a warning when route files export anything beyond `Route` ("these exports will not be code-split and will increase your bundle size"). Both pointed to the same fix: extract `DataEntryPage` to `web/src/components/dataEntry/DataEntryPage.tsx` so the route file owns only `Route` + a thin `RouteComponent` wrapper, and the test renders `DataEntryPage` directly against the component contract.
- Vitest `vi.mock('@tanstack/react-router')` required `importOriginal()` because the plugin-transformed route module references other router exports (e.g. `lazyRouteComponent`) at module init — a bare factory failed with `No "lazyRouteComponent" export is defined on the mock`.
- `react-refresh/only-export-components` fires on file-based route files because they export `Route` (non-component) plus a component (the local `RouteComponent`). The baseline lint count at branch-tip was 23 errors (verified by stashing the diff and re-running lint). Suppressed the rule on the local `RouteComponent` declaration with a focused `eslint-disable-next-line` so this story does not raise the count.

### Completion Notes List

- AC-1 (Schema fetch via DynamicComponent): satisfied implicitly — `DynamicComponent` already owns the authenticated `GET /api/designers/{designerId}/versions/{version}` fetch with `staleTime: 300_000` (unchanged in this story); both new host surfaces (RepeaterRowDrawer + DataEntryPage) mount it with `designerId`/`version` and pass through the standard `submitRef`/`onValidityChange`/`onReadyChange` contract.
- AC-2 (All 14 component types render): preserved — no changes to `ElementRenderer.tsx`'s 14-leaf render paths, the Repeater rendering, CSS parsing, or DynamicDropdown. Only the embedded `RepeaterRowDrawer` was extracted.
- AC-3 (Visibility conditions respected): preserved — `computeVisibility` and the visibility wiring inside `DynamicComponent` are untouched. The new data-entry route mounts `DynamicComponent` directly, so visibility behavior is inherited end-to-end.
- AC-4 (Repeater row editing wired): `RepeaterRowDrawer` now mounts `DynamicComponent` with `initialData`/`onSave` and the external `submitRef`/`isReady`/`isValid` contract. The footer Save button is disabled until both ready and valid; clicking it fires `submitRef.current?.()`, which runs DynamicComponent's validate-then-onSave path. Escape / backdrop click continue to close (existing behavior, preserved by extracting the function wholesale, not rewriting it). Empty `designerId` shows a friendly "No row designer configured" message and the Save button stays disabled.
- AC-5 (External Save button via submitRef): `DataEntryPage` owns a `submitRef`, an external Save button gated on `isReady && isValid && !isSaving`, and a stubbed `onSave` (`toast.success(t('data.entry.saveStub'))` only — payload param dropped after review patch #4 to avoid Sentry/Datadog console breadcrumb leak) — Epic 6 will swap the stub for the real `useMutation` call.
- Circular-import fix: `DynamicComponent.tsx → ElementRenderer.tsx → DynamicComponent.tsx` is now broken — `RepeaterRowDrawer.tsx` is the only module that imports `DynamicComponent` from inside the renderer family. `ElementRenderer.tsx` no longer imports `DynamicComponent`.
- `X` icon was removed from the `lucide-react` import in `ElementRenderer.tsx` (it was used only by the extracted drawer).
- Route file kept minimal — only exports `Route` — so the tanstack-router-plugin's autoCodeSplitting produces a clean `data._designerId-*.js` chunk (1.18 kB gzipped) without "exports will not be code-split" warnings.
- Lint baseline at branch-tip is 23 errors (not the 27 quoted in the story spec); story diff introduces 0 new errors. Verified empirically.
- Frontend tests: 61/61 pass (53 baseline + 6 new RepeaterRowDrawer + 2 new DataEntryPage). No backend changes — backend test count stays at 234.
- Review patches applied (2026-05-24): all 9 `[Review][Patch]` items addressed — i18n keys for the drawer, slide-out animation on Save, `canEdit` gated on `rowDesignerId`, payload-leak `console.log` removed, mousedown-drag-out close guard, and 4 test upgrades (Save-click in DataEntryPage; ordering + payload assertion on drawer Save; fake-timer Escape assertion; initialData propagation). Post-patch counts: lint 23/23 (baseline preserved), tests **63/63** (added 2: `initialData propagation` in drawer + `clicking Save invokes submitRef → toast` in DataEntryPage), `pnpm build` clean, `data._designerId-*.js` chunk now 1.10 kB gzipped (was 1.18 kB — `console.log` removal trimmed the bundle).

### File List

**New files**
- `web/src/components/designer/RepeaterRowDrawer.tsx` — extracted from `ElementRenderer.tsx`, wired to `DynamicComponent` with `submitRef`/`isReady`/`isValid` and a Save/Cancel footer; renders an empty-state message when `designerId === ''`. Review patches: i18n (`useTranslation()` + `designer.repeaterDrawer.*` keys), Save wrapper now calls `handleClose()` (slide-out animation), `overlayMouseDownRef` guards against mousedown-drag-out closing.
- `web/src/components/dataEntry/DataEntryPage.tsx` — receives `designerId` as a prop, mounts `DynamicComponent` with the external-Save-button contract, and stub-handles `onSave` (toast only — payload param removed in review patch #4 to avoid console-breadcrumb leak).
- `web/src/routes/_app/data.$designerId.tsx` — TanStack file-based route at `/_app/data/$designerId`; thin wrapper that reads `designerId` via `Route.useParams()` and forwards to `<DataEntryPage />`.
- `web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx` — 7 Vitest unit tests (mounts DC, **forwards initialData (new in review patch #8)**, empty-designerId message + disabled Save, disabled-when-not-ready, disabled-when-invalid, **Save-click invokes host onSave with payload then closes via 350ms safety timer (rewired in review patches #6+#7)**, **Escape triggers closing-animation + onClose after 350ms safety timer (rewired in review patch #7)**).
- `web/src/routes/_app/-data.$designerId.test.tsx` — 3 Vitest tests against `DataEntryPage` (designerId is forwarded to DC; external Save gates on `isReady && isValid`; **Save-click fires toast via mock submitRef→captured onSave path (new in review patch #5)**).

**Modified files**
- `web/src/components/designer/ElementRenderer.tsx` — removed the inline `RepeaterRowDrawer` function body (~lines 1849–1929); added `import RepeaterRowDrawer from './RepeaterRowDrawer'`; dropped `X` from the `lucide-react` import (it was only used by the extracted drawer). Review patch #3: `canEdit` now gated on `rowDesignerId !== ''` (mirroring `canAdd`), and `editTitle` surfaces "Configure Designer ID first" hover hint.
- `web/src/lib/i18n/locales/en.json` — added `data.entry.{newRecord, save, saving, saveStub}` block. Review patch #1: added `designer.repeaterDrawer.{titleEdit, titleAdd, closeAria, noDesigner, cancel, save}` keys.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story 3-9 status: `ready-for-dev` → `in-progress` → `review` → `in-progress` (review patches) → `review`; updated `last_updated`.

### Change Log

- 2026-05-24 — Story 3.9 initial implementation. Extracted `RepeaterRowDrawer` to its own module to break the would-be `DynamicComponent ↔ ElementRenderer` circular import, wired it to `DynamicComponent` with the external-submit-ref + ready/valid gating contract, and added a data-entry route (`/_app/data/$designerId`) that hosts `DynamicComponent` with a stub `onSave` (Epic 6 will swap for the real mutation). Added 8 Vitest tests (6 drawer + 2 page); 61/61 pass. No backend changes.
- 2026-05-24 — Addressed code review findings — 9 `[Review][Patch]` items resolved (story had been moved to `review` prematurely; flipped back to `in-progress` to apply patches before re-marking). Source: i18n in `RepeaterRowDrawer` (+6 `designer.repeaterDrawer.*` keys), Save wrapper now uses `handleClose()` so the drawer animates out on save, `canEdit` gated on `rowDesignerId !== ''`, payload `console.log` removed from `DataEntryPage` (PII/breadcrumb leak risk), mousedown-drag-out overlay guard added. Tests: rewired drawer Save-click test to exercise the `(data) => { onSave(data); handleClose() }` wrapper with payload assertion; converted Escape test to fake timers + `onClose` assertion; added `initialData` propagation test; added DataEntryPage Save-click test covering the toast stub. Post-patch: 63/63 frontend tests pass, lint 23/23 baseline, build clean.
- 2026-05-24 — Re-review of the patches commit (`7989fd1`) caught that the mousedown-drag-out guard was incomplete: the panel's `onClick={e.stopPropagation()}` could leave `overlayMouseDownRef` stuck at `true`, which would then close the drawer on the next legitimate panel-down → overlay-up drag, exactly the bug Patch #9 was meant to prevent. Fix: panel `onMouseDown` now resets the ref to `false` before stopping propagation. Added a regression test exercising the failing two-sequence interaction. Also fixed two spec/doc drifts: Task 3 still mandated the old `handleSave(payload)` signature + `console.log`, and Task 6 still claimed 61/61 tests instead of 63/63. Post-re-review: 64/64 frontend tests pass (+1 drag-out regression), lint 23/23 baseline, build clean (chunk unchanged at 1.10 kB gzipped). Status → done.

### Review Findings

3-reviewer parallel pass (Blind Hunter, Edge Case Hunter, Acceptance Auditor) on commit `a119bed`. 38 raw findings → 9 patch + 10 defer + 10 dismissed + 0 decision-needed.

- [x] [Review][Patch] RepeaterRowDrawer ships hardcoded English strings — `Edit row`, `Add row`, `Close drawer`, `No row designer configured…`, `Cancel`, `Save` are literals; rest of the app uses `useTranslation()`. [web/src/components/designer/RepeaterRowDrawer.tsx:51,80,101,111,119] — fixed: added `useTranslation()` and 6 new `designer.repeaterDrawer.*` keys in `en.json`
- [x] [Review][Patch] Drawer Save bypasses slide-out animation — `onSave` wrapper calls `onClose()` directly instead of `handleClose()`, so the drawer pops out without the 300 ms transition while Cancel/Esc/overlay animate. [web/src/components/designer/RepeaterRowDrawer.tsx:91-94] — fixed: wrapper now calls `handleClose()`, so Save animates out like Cancel/Esc
- [x] [Review][Patch] `canEdit` not gated on `rowDesignerId !== ''` — pre-existing rows with an empty `rowDesignerId` open the drawer to the empty-state and Save stays disabled forever; user can't escape except Cancel. `canAdd` already has this gate. [web/src/components/designer/ElementRenderer.tsx:1710] — fixed: `canEdit = bound && rowDesignerId !== ''`, and `editTitle` now surfaces "Configure Designer ID first" hover hint mirroring `addTitle`
- [x] [Review][Patch] `console.log` of payload ships full record (PII risk) — runs on every Save in any environment, picked up by Sentry/Datadog console breadcrumbs by default. [web/src/components/dataEntry/DataEntryPage.tsx:22] — fixed: removed `console.log`; `handleSave` now drops the payload parameter entirely (TS bivariance handles the signature) — stub only toasts
- [x] [Review][Patch] DataEntryPage test never clicks Save — only flips ready/valid and asserts button enabled; the entire `handleSave` stub path (toast + console.log) is uncovered. [web/src/routes/_app/-data.$designerId.test.tsx] — fixed: added Save-click test that captures the mock's `onSave` prop, fires it via `submitRef`, and asserts `toast.success('data.entry.saveStub')` was called once
- [x] [Review][Patch] Drawer Save test doesn't assert host `onSave`+`onClose` ordering — `submitSpy` proves button→ref plumbing, but mock submit never invokes the captured `onSave` prop, so the `(data) => { onSave(data); onClose() }` wrapper is unverified. Deleting the wrapper keeps tests green. [web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx ≈l. 142-166] — fixed: mock now captures `onSave`; the rewired Save-click test asserts `onSave(payload)` fires and `onClose` runs after the 350ms safety timer (deleting the `(data) => { onSave(data); handleClose() }` wrapper now fails the test)
- [x] [Review][Patch] Drawer Escape test only asserts class flip — doesn't use fake timers + advance 350 ms + assert `onClose` was called. Stripping the timeout in `useEffect[closing,onClose]` and the `onTransitionEnd` handler both leave the test green. [web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx ≈l. 168-186] — fixed: Escape test now uses `vi.useFakeTimers()`, asserts `onClose` is NOT called synchronously, then advances 350ms and asserts it fired exactly once — stripping the safety timer breaks the test
- [x] [Review][Patch] `initialData` propagation untested — mock captures it as `data-initial-data` but no test asserts on the value; edit-mode wiring is not verified. [web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx ≈l. 332-344] — fixed: added a dedicated test that mounts edit-mode with `initialData={{name:'Alice', age:42}}` and parses `data-initial-data` to assert deep-equal
- [x] [Review][Patch] Drawer mousedown-drag-out closes drawer — mousedown inside an input, drag outside, release → bubbling click on overlay fires `handleClose` and loses unsaved input. Guard click on mousedown target ref. [web/src/components/designer/RepeaterRowDrawer.tsx:60-67] — fixed: overlay tracks `overlayMouseDownRef` set only when `mousedown.target === currentTarget`; the `click` handler now closes only when both mousedown AND click originated on the overlay itself, and the inner panel additionally stops mousedown propagation for belt-and-braces
- [x] [Review][Defer] `isSaving` sticks `true` if `handleSave` throws — no try/finally; minor for the stub, real once Epic 6 wires async mutation. [web/src/components/dataEntry/DataEntryPage.tsx:19-27] — deferred to Epic 6 mutation wiring
- [x] [Review][Defer] No double-submit lock on external Save — rapid double-click bypasses `isSaving` (batched), fires `submitRef` twice. Concrete risk once Epic 6 lands a real POST. [web/src/components/dataEntry/DataEntryPage.tsx:33-46] — deferred to Epic 6 mutation wiring (use `mutation.isPending` synchronously)
- [x] [Review][Defer] Drawer lacks focus trap, `role="dialog"`, `aria-modal`, no focus restore — Tab walks out into the form behind, screen readers don't announce modal semantics. Pre-existing — was a TODO placeholder before this story. [web/src/components/designer/RepeaterRowDrawer.tsx:54-83] — deferred to Story 7.4 (Accessibility Compliance) keyboard-nav pass
- [x] [Review][Defer] Nested Repeater drawers all close on a single Escape — window-level keydown listener has no topmost-only or `stopPropagation` check; nested-Repeater rows lose edits on Escape. [web/src/components/designer/RepeaterRowDrawer.tsx:34-40] — deferred; esoteric path until nested repeaters are common
- [x] [Review][Defer] No fallback navigation when `designerId` is unknown/deleted — DynamicComponent renders bare "Component unavailable"; Save stays disabled forever, no Back link. [web/src/components/dataEntry/DataEntryPage.tsx + web/src/components/designer/DynamicComponent.tsx] — deferred to Epic 6 data-entry UX pass
- [x] [Review][Defer] No `valid→invalid` regression test — mock buttons only flip flags `true`; a bug ignoring later `onValidityChange(false)` would slip through. [web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx] — deferred test-coverage polish
- [x] [Review][Defer] Route has no `?version=` search param — only latest reachable. Spec AC allows "(or latest)"; flagged for future pinning support. [web/src/routes/_app/data.$designerId.tsx] — deferred; intentional latest-only per AC-1 parenthetical
- [x] [Review][Defer] Save button has no `aria-busy` / no `aria-live` region — `Saving…` label flip alone isn't announced. Coupled with the dead `isSaving` toggle today. [web/src/components/dataEntry/DataEntryPage.tsx:39-46] — deferred to Story 7.4 a11y pass
- [x] [Review][Defer] `transitionend`+350 ms-timer double-`onClose` race — theoretical: cleanup runs on unmount before 350 ms in current parent. Safe today; documented for future hosts that don't unmount synchronously. [web/src/components/designer/RepeaterRowDrawer.tsx:45-72] — deferred; speculative
- [x] [Review][Defer] `react-refresh/only-export-components` disable on `RouteComponent` masks HMR breakage — edits lose state on hot reload. Documented design choice in the file. [web/src/routes/_app/data.$designerId.tsx:8-13] — deferred; documented limitation, not a correctness issue

### Review Findings — Re-Review of Patches Commit (2026-05-24)

3-reviewer parallel pass (Blind Hunter, Edge Case Hunter, Acceptance Auditor) on commit `7989fd1` (the patches commit itself). 27 raw findings → 3 patch + 3 defer + 0 decision-needed + 11 dismissed (mostly already-deferred items re-surfaced or test-fidelity nitpicks).

- [x] [Review][Patch] Overlay mousedown-ref guard is incomplete — same drag-out bug Patch #9 was supposed to prevent still fires through a stale `overlayMouseDownRef = true` [web/src/components/designer/RepeaterRowDrawer.tsx:68-76,83] — Sequence: (1) mousedown on overlay sets ref=true, drag into panel, release on panel → panel's `onClick={e.stopPropagation()}` swallows the click, ref is never reset; (2) mousedown inside an input on the panel → panel's `onMouseDown={e.stopPropagation()}` blocks overlay's onMouseDown from running, ref stays at true; drag out, release on overlay → overlay onClick sees `target===currentTarget && ref===true` → `handleClose()` fires and edits are lost. Sources: blind+edge. — fixed: panel `onMouseDown` now resets `overlayMouseDownRef.current = false` BEFORE `e.stopPropagation()`, so every panel mousedown clears any stale `true` left over from an earlier overlay-down → panel-up sequence. Added regression test "mousedown-inside-panel + drag-to-overlay + release-on-overlay does NOT close (drag-out guard, regression of patch #9)" that exercises the exact failing sequence and would fail without the reset.
- [x] [Review][Patch] Spec Task 3 still mandates the old `handleSave(payload)` signature that patch #4 deliberately removed [_bmad-output/implementation-artifacts/3-9-dynamiccomponent-renderer-for-data-entry.md:122-131] — The Task 3 code block prescribes `useCallback((payload: Record<string, unknown>) => { ... console.log('[data-entry] payload:', payload) ... })`, but `DataEntryPage.tsx:19-22` now exports `useCallback(() => { ... })` (payload arg dropped to prevent breadcrumb leak). Source: auditor. — fixed: Task 3 code block updated to drop the payload parameter and the `console.log` line, with an inline comment explaining the TS bivariance rationale and the Epic 6 reinstatement path so future devs reading the spec don't reintroduce the leak.
- [x] [Review][Patch] Spec Task 6 test-count claim is stale (61/61) — should be 63/63 post-patch [_bmad-output/implementation-artifacts/3-9-dynamiccomponent-renderer-for-data-entry.md:188] — Task 6 line still says `pnpm run test — 61/61 pass (53 baseline + 6 RepeaterRowDrawer + 2 DataEntryPage)`, contradicting the Completion Notes (line 308) and Change Log (line 327) which both say 63/63 (+initialData propagation drawer test, +Save-click DataEntryPage test). Source: auditor. — fixed: Task 6 line updated to `63/63 pass (53 baseline + 7 RepeaterRowDrawer + 3 DataEntryPage)` with a footnote noting the subsequent re-review added a drag-out regression test (now 64/64 total).
- [x] [Review][Defer] `isSaving` is dead-on-arrival in the stub — `setIsSaving(true); toast; setIsSaving(false)` collapse in a single React batch, so the `!isSaving` gate never engages and held-Enter / rapid-double-click can fire `submitRef` repeatedly [web/src/components/dataEntry/DataEntryPage.tsx:19-22] — deferred; partially overlaps the existing deferred "No double-submit lock on external Save" item — Epic 6 mutation wiring will use `mutation.isPending` (synchronously updated by TanStack) which makes the gate real.
- [x] [Review][Defer] Patch #3 (`canEdit` gating + `editTitle` hint) lands without unit-test coverage [web/src/components/designer/ElementRenderer.tsx:1710,1721-1725] — no test in `web/src/components/designer/__tests__/` exercises `RepeaterInteractive`, so a regression restoring `canEdit = bound` would not be caught. Deferred test-coverage polish; pre-existing pattern (RepeaterInteractive has no direct unit-test file).
- [x] [Review][Defer] Mock test fidelity: drawer mock captures `onSave` via `useEffect`, and both rewired tests collapse the real DC's submit→onSave chain into a synchronous mock invocation [web/src/components/designer/__tests__/RepeaterRowDrawer.test.tsx + web/src/routes/_app/-data.$designerId.test.tsx] — works today because the ready/valid click flushes effects before Save is clicked, but the "end-to-end" framing in test names overstates what's verified. Deferred test-fidelity polish.

**Dismissed (11)** — not worth tracking individually, but for the record:
- Double-`onClose` race (handleClose → both transitionend handler + 350ms timer call onClose): same theoretical race already deferred as item #9 last pass; current host (RepeaterInteractive setEditing(null)) cleans up the timer via unmount cleanup synchronously, so net fire count stays at 1.
- `canEdit` traps legacy rows for inspection: empty-state doesn't actually render row data, so opening the drawer pre-patch was already a dead-end — the patch just surfaces the dead-end at the button level.
- Tests assert on i18n keys not translated strings: standard pattern across the project; `t: (key) => key` mock is intentional.
- i18n keys only added to English locale: only `en.json` exists in `web/src/lib/i18n/locales/`; no other locales to update.
- `onTransitionEnd` unguarded against non-slide transitions: already guarded by `closing && propertyName === 'transform'`.
- Sprint-status `last_updated` is free-form English: pre-existing pattern, not machine-parsed.
- Patch #6 doesn't assert call ordering of `onSave`+`handleClose`: ordering is not a meaningful contract — both must run, and they're synchronous.
- "Configure Designer ID first" hardcoded English in ElementRenderer: consistent with other untranslated tooltips in the same file (`Configure Bind to first`, `Minimum/Maximum rows reached`); pre-existing i18n debt.
- Overlay no longer closes on synthetic clicks lacking mousedown: overlay close is non-essential — X / Cancel / Escape remain.
- TS bivariance enables `() => void` to be assigned where `(p: X) => void` is expected: deliberate stub design, documented in code comment.
- "Lint 23/23 baseline preserved" claim self-reported, not from CI artifact: review-hygiene meta-finding, out of scope.
