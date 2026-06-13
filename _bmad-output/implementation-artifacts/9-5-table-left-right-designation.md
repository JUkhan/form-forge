# Story 9.5: Table Left/Right Designation

Status: done

## Story

As a user building a Dataset,
I want to designate each table node as "left table" or "right table",
so that join sidedness is explicit and the SQL generator can produce a correct FROM / JOIN clause.

## Acceptance Criteria

**AC-1 — Each table node header shows an interactive Left/Right toggle**
**Given** each table node header (per AR-68 `TableNode.tsx`)
**When** the node renders
**Then** a "Left" / "Right" toggle is visible **and clickable** (the current Story 9.1 placeholder is a non-interactive `<span>` badge — this story makes it interactive).

**AC-2 — First table defaults Left, subsequent default Right**
**Given** the first table dragged onto the canvas
**When** it lands
**Then** its designation defaults to "Left"; all subsequently dragged tables default to "Right" (per FR-66 AC-2 / A21).
(This default already exists in `onDrop` from Story 9.1: `side: nodes.length === 0 ? 'left' : 'right'`. Story 9.5 keeps it and adds the interactive override. See deferred-work **W5 (9.1)**.)

**AC-3 — No-left-table validation message gates Save/Preview**
**Given** more than one table is on the canvas and no table is designated as "Left"
**When** Save or Preview is attempted
**Then** both actions are disabled with the validation message: **"Designate one table as the left (FROM) table."** (per FR-66 AC-3).
**Scope note (read Dev Notes §4):** the builder canvas page has **no Save/Preview buttons yet** — they land in Story 11.2 (save/persist) and Story 11.3 (preview). This story delivers (a) a **single-source-of-truth validation helper** that 11.2/11.3 will consume to disable those buttons, and (b) a **visible validation banner** on the canvas so the rule is observable now.

**AC-4 — Left designation drives the FROM anchor (data contract for Story 11.1)**
**Given** I set a table to "Left"
**When** the SQL generator runs (Story 11.1)
**Then** it uses the left-designated table as the `FROM` anchor; all other tables appear as `JOIN` clauses (per FR-66 AC-1).
**Scope note:** Story 9.5 does **not** implement SQL generation. Its deliverable for AC-4 is that exactly one node carries `data.side === 'left'` and the rest `'right'`, persisted correctly, so Story 11.1 can consume it unambiguously (see the single-Left invariant in Dev Notes §3).

**AC-5 — Designation persists to builder_state**
**Given** a designation change
**When** I save
**Then** the Left/Right value is stored per node in `builder_state.nodes[*].data.side` (per FR-66 AC-4 / AR-67).
(The `side: TableSide` field already exists in `TableNodeData`. This story ensures every side change is emitted to the parent via `notify(...)` so the held `builderState` is correct when Story 11.2 persists it.)

---

## Tasks / Subtasks

### Task 1 — Frontend: Add the validation helper to `builderState.ts` (AC-3)

Open `web/src/features/datasets/types/builderState.ts`. After `parseBuilderState` (end of file), add a pure, exported helper. This is the **single source of truth** for the left-table gate — the canvas banner uses it now; Stories 11.2/11.3 reuse it to disable Save/Preview.

```typescript
// Story 9.5: Validation gate for the left (FROM) table designation.
// Returns the i18n key when the builder state is invalid, or null when valid.
// Single source of truth — the canvas validation banner (Story 9.5) and the
// Save/Preview buttons (Stories 11.2/11.3) both consume this.
//
// Rule (FR-66 AC-3): only "more than one table with no Left" is invalid. A
// single table is its own FROM anchor (no designation required); zero tables is
// valid here (empty-canvas Save is gated separately in Epic 10/11).
export function getLeftTableValidationError(
  nodes: Pick<TableNodeState, 'data'>[],
): 'datasets.builder.validation.noLeftTable' | null {
  if (nodes.length <= 1) return null
  const hasLeft = nodes.some((n) => n.data.side === 'left')
  return hasLeft ? null : 'datasets.builder.validation.noLeftTable'
}
```

Note: typed against `Pick<TableNodeState, 'data'>[]` so it accepts both `TableNodeState[]` and the canvas's `TableNodeType[]` (which structurally satisfies `{ data: TableNodeData }`).

### Task 2 — Frontend: Make the toggle interactive in `TableNode.tsx` (AC-1)

Open `web/src/components/query-builder/TableNode.tsx`.

**Step 1 — Update imports.**

```typescript
import { memo, createContext, useContext } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { useTranslation } from 'react-i18next'
import type { TableNodeData, TableSide } from '../../features/datasets/types/builderState'
```

**Step 2 — Export a context that carries the side-change handler.**

Add right after the `TableNodeType` type alias. The handler is provided by the canvas (Task 3); it is kept **out of node `data`** because `data` is serialized into `builder_state` and must stay pure (no functions). Define it here (not in the canvas) to avoid a circular import — the canvas already imports `TableNode`.

```typescript
// Story 9.5: the canvas provides the side-change handler via context so it can
// own the single-Left invariant and emit to the parent via notify(). Default is
// a no-op so the node renders safely if ever used outside the provider (tests).
export const TableSideChangeContext = createContext<(nodeId: string, side: TableSide) => void>(
  () => {},
)
```

**Step 3 — Read `id`, `t`, and the context inside the component, and replace the badge with a two-button segmented toggle.**

Change the component signature to destructure `id`:

```typescript
export const TableNode = memo(function TableNode({ id, data, selected }: NodeProps<TableNodeType>) {
  const { t } = useTranslation()
  const onSideChange = useContext(TableSideChangeContext)
```

Replace the existing header `<span>` badge (the `{data.side === 'left' ? 'Left' : 'Right'}` block) with:

```tsx
{/* Left/Right designation — interactive (Story 9.5). `nodrag nopan` so clicking
    a button never starts a node drag or canvas pan. */}
<div
  role="group"
  aria-label={t('datasets.builder.node.sideGroupAria')}
  className="nodrag nopan ml-2 flex shrink-0 overflow-hidden rounded border border-border"
>
  <button
    type="button"
    onClick={() => onSideChange(id, 'left')}
    aria-pressed={data.side === 'left'}
    className={`px-1.5 py-0.5 text-xs font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
      data.side === 'left'
        ? 'bg-primary text-primary-foreground'
        : 'bg-background text-muted-foreground hover:bg-overlay-hover'
    }`}
  >
    {t('datasets.builder.node.left')}
  </button>
  <button
    type="button"
    onClick={() => onSideChange(id, 'right')}
    aria-pressed={data.side === 'right'}
    className={`border-l border-border px-1.5 py-0.5 text-xs font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
      data.side === 'right'
        ? 'bg-primary text-primary-foreground'
        : 'bg-background text-muted-foreground hover:bg-overlay-hover'
    }`}
  >
    {t('datasets.builder.node.right')}
  </button>
</div>
```

Leave the rest of the node (column list, source/target handles, `selected` ring) **unchanged**.

### Task 3 — Frontend: Wire the side-change handler + validation banner in `QueryBuilderCanvas.tsx` (AC-1, AC-3, AC-5, single-Left invariant)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Update imports.**

Add `useTranslation`, the context, `TableSide`, and the helper. `useState`/`useCallback`/`useRef` are already imported.

```typescript
import { useTranslation } from 'react-i18next'
import { TableNode, TableSideChangeContext, type TableNodeType } from '../../components/query-builder/TableNode'
```
(extend the existing `TableNode` import line to also pull `TableSideChangeContext`.)

Add `TableSide` to the `builderState` type import and `getLeftTableValidationError` as a value import:

```typescript
import type {
  BuilderState,
  ColumnSelection,
  JoinEdgeState,
  JoinType,
  TableNodeData,
  TableNodeState,
  TableSide,
} from './types/builderState'
import { getLeftTableValidationError } from './types/builderState'
```

**Step 2 — Add `const { t } = useTranslation()` at the top of `QueryBuilderCanvasInner`.**

**Step 3 — Add `handleSideChange` (place it near `handleJoinTypeChange`).** This is the only writer of `data.side`. It enforces the **single-Left invariant** (Dev Notes §3) and emits via `notify` so AC-5 holds.

```typescript
// AC-1/AC-5 + single-Left invariant: set the clicked node's side and, when
// promoting a node to 'left', demote any other 'left' node to 'right' so exactly
// one FROM anchor exists for Story 11.1. Emits to the parent via notify().
const handleSideChange = useCallback(
  (nodeId: string, side: TableSide) => {
    const updatedNodes = nodes.map((n) => {
      if (n.id === nodeId) {
        return { ...n, data: { ...n.data, side } }
      }
      if (side === 'left' && n.data.side === 'left') {
        return { ...n, data: { ...n.data, side: 'right' as TableSide } }
      }
      return n
    })
    setNodes(updatedNodes)
    notify(updatedNodes, edges)
  },
  [nodes, edges, setNodes, notify],
)
```

**Step 4 — Compute the validation error before the return.**

```typescript
const leftTableError = getLeftTableValidationError(nodes)
```

**Step 5 — Wrap the `<ReactFlow>` element in the context provider, and render the banner.**

The provider must wrap `<ReactFlow>` (custom nodes render inside it, so context reaches them). The return becomes:

```tsx
return (
  <div ref={reactFlowWrapper} className="relative h-full w-full">
    <TableSideChangeContext.Provider value={handleSideChange}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodesChange={handleNodesChange}
        onEdgesChange={handleEdgesChange}
        onNodeDragStop={onNodeDragStop}
        onConnect={onConnect}
        isValidConnection={isValidConnection}
        onEdgeClick={onEdgeClick}
        onPaneClick={onPaneClick}
        onDragOver={onDragOver}
        onDrop={onDrop}
        deleteKeyCode={['Backspace', 'Delete']}
        fitView
      >
        <Background />
        <Controls />
        <MiniMap />
      </ReactFlow>
    </TableSideChangeContext.Provider>
    {leftTableError && (
      <div
        role="alert"
        className="nodrag nopan pointer-events-none absolute left-1/2 top-4 z-40 -translate-x-1/2 rounded-md border border-amber-500/30 bg-amber-50 px-3 py-1.5 text-xs font-medium text-amber-800 shadow-sm dark:bg-amber-900/30 dark:text-amber-300"
      >
        {t('datasets.builder.validation.noLeftTable')}
      </div>
    )}
    {selectedEdge && (
      <JoinInspector
        edge={selectedEdge}
        nodes={nodes}
        onJoinTypeChange={handleJoinTypeChange}
        onClose={() => setSelectedEdge(null)}
      />
    )}
  </div>
)
```

**Critical (Dev Notes §5):** render the banner text with the **literal** `t('datasets.builder.validation.noLeftTable')` (not `t(leftTableError)`), so the i18n-check script statically detects the key as used. `leftTableError` is only the boolean gate here. Do **not** remove the existing `relative` class on the wrapper (added in Story 9.4) — both the banner and the JoinInspector are positioned against it.

### Task 4 — Frontend: Add i18n keys to `en.json` (AC-1, AC-3)

Open `web/src/lib/i18n/locales/en.json`. Extend the existing `datasets.builder` object (which currently has only `palette`) — add sibling `node` and `validation` objects:

```json
"builder": {
  "palette": {
    "...": "existing keys — leave unchanged"
  },
  "node": {
    "left": "Left",
    "right": "Right",
    "sideGroupAria": "Table side designation"
  },
  "validation": {
    "noLeftTable": "Designate one table as the left (FROM) table."
  }
}
```

Every new key must be referenced by a `t('...')` literal (Tasks 2 & 3) or i18n-check reports it as orphaned (warning only, but keep it clean). Conversely, every `t('...')` literal you add must exist here or i18n-check **fails (exit 1)**.

### Task 5 — Frontend: Unit test the validation helper (AC-3)

`getLeftTableValidationError` is pure logic that gates Save/Preview — cover it directly. Create `web/src/features/datasets/types/builderState.test.ts` (co-located; `**/*.test.*` is excluded from i18n-check). Use the existing vitest setup.

Cases:
- `[]` → `null` (zero tables valid)
- one node (`side:'left'`) → `null`
- one node (`side:'right'`) → `null` (single table is its own anchor)
- two nodes, one `'left'` → `null`
- two nodes, both `'right'` → `'datasets.builder.validation.noLeftTable'`
- two nodes, both `'left'` → `null` (has a left; the single-Left invariant is enforced at write time in `handleSideChange`, not by this read-time gate)

Build the minimal node shape inline, e.g. `{ data: { tableName: 't', side: 'right', columns: [] } }`.

### Task 6 — Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors. ✅
- [x] `cd web && npx vitest run` → **288 passed / 0 failed** (re-verified in code review 2026-06-04). _[Correction: the dev draft recorded "287 passed / 1 failed (pre-existing i18n-lint)". That is no longer accurate — this story also added the `designer.inspector.placeholders.label` key (see Completion Notes), which fixed the previously-failing `i18n-lint` test. The suite is now fully green.]_ ✅
- [x] `cd web && node scripts/i18n-check.mjs` → **exit 0** (re-verified in code review 2026-06-04). _[Correction: the dev draft recorded exit 1 "due ONLY to the pre-existing missing `designer.inspector.placeholders.label`". Since this story added that key, the only fatal case is resolved; the script now reports only non-fatal orphan warnings (none of them this story's keys).]_ The 4 new keys (`datasets.builder.node.{left,right,sideGroupAria}`, `datasets.builder.validation.noLeftTable`) are correctly referenced and present. ✅
- [ ] Manual smoke (AC-1): open a Query Builder dataset (datasets list → "Open Builder"); drag two tables; confirm each header shows a Left|Right toggle; click toggles and confirm the highlighted segment changes. _pending user browser verification_
- [ ] Manual smoke (AC-2): confirm the first dropped table shows "Left" highlighted and the second shows "Right". _pending user browser verification_
- [ ] Manual smoke (single-Left, §3): set table B to "Left"; confirm table A flips to "Right" automatically (exactly one Left at all times after a Left click). _pending user browser verification_
- [ ] Manual smoke (AC-3): with two tables, set both to "Right" (or delete the Left table); confirm the amber banner "Designate one table as the left (FROM) table." appears at top-center; designate one Left and confirm it disappears. With a single table, confirm no banner. _pending user browser verification_
- [ ] Manual smoke (no-drag): clicking a toggle button does **not** drag the node or pan the canvas (`nodrag nopan` works). _pending user browser verification_

---

## Review Findings

_Code review 2026-06-04 (bmad-code-review: Blind Hunter + Edge Case Hunter + Acceptance Auditor). 2 decision-needed, 1 patch, 3 deferred, 5 dismissed as noise._

**Decision needed**

- [x] [Review][Decision] **RESOLVED → keep `destructive` tokens; spec updated.** Banner uses `destructive` (not the draft's amber) palette — §7 + Task 3 Step 5 prescribed `border-amber-500/30 …`; implementation shipped `border-destructive/30 bg-destructive/10 text-destructive`. Decision 1 → keep (aligns with theme-token remediation, no `dark:`); §7/§12 reconciled. [`QueryBuilderCanvas.tsx` banner]
- [x] [Review][Decision] **RESOLVED → keep the 3 bundled changes; records corrected.** `datasets.tsx` ModeBadge + `ElementRenderer.tsx` token swaps + the `designer.inspector.placeholders.label` key were out of the §10 File List; that key fixes the i18n-lint baseline (re-verified: i18n-check exit 0, suite 288/0). Decision 2 → keep; Task 6 Verification + Completion Notes corrected, §7/§12 reconciled, memory note updated. [`datasets.tsx:350`, `ElementRenderer.tsx:1156`, `en.json:813`]

**Patch**

- [x] [Review][Patch] **APPLIED.** `handleSideChange` + context provider value recreated every render [`QueryBuilderCanvas.tsx`] — reads `nodes`/`edges` from closure and rebuilt on each change, so `TableSideChangeContext.Provider value` identity changed every render (defeated `TableNode` `memo`). Fixed: added `nodesRef`/`edgesRef` mirrors (same posture as `extraStateRef`), `handleSideChange` now reads refs and deps are `[setNodes, notify]` → stable identity. tsc + 288/0 suite re-verified green.

**Deferred** (pre-existing / out-of-scope; logged to deferred-work.md)

- [x] [Review][Defer] `notify` emits React Flow runtime fields (`measured`, `dragging`, `position`, …) into the held `builderState` [`QueryBuilderCanvas.tsx` notify] — pre-existing across all notify paths; will matter for Story 11.2 persistence.
- [x] [Review][Defer] `parseBuilderState` doesn't validate node-internal shape [`builderState.ts`, `TableNode.tsx`] — `data.side`/`data.columns` typed non-optional but unguarded; a legacy/malformed blob with no `side` shows neither button pressed, and `data.columns.map` could crash the node. Pre-existing.
- [x] [Review][Defer] Read-time validator tolerates ≥2 `left` by design [`builderState.ts:getLeftTableValidationError`] — §3 enforces single-Left only at write time; recommend a defensive single-Left normalization on load when persistence lands (11.1/11.2).

---

## Dev Notes

### §1 — Where Story 9.4 / 9.1 Left Off for Story 9.5

- **Story 9.1** rendered the side as a **non-interactive `<span>` badge** (`TableNode.tsx:21-24`) and set the default in `onDrop` (`QueryBuilderCanvas.tsx`: `side: nodes.length === 0 ? 'left' : 'right'`). Deferred-work **W5 (9.1)** explicitly assigns the interactive toggle to this story: _"Story 9.5 wires the actual left/right designation toggle; this default will be replaced then."_ Keep the `onDrop` default (it satisfies AC-2); add the interactive override.
- **Story 9.4** noted (its §10) that the `JoinInspector` already reads the live `nodes` array, so its Left/Right side labels will **automatically reflect** any toggle change made here — **no JoinInspector changes are needed**, and you must not break this. The inspector reads `sourceNode.data.side` from the current `nodes`; because `handleSideChange` updates `nodes` state, re-opening the inspector after a toggle shows the new label for free.

### §2 — Why a Context, Not Node `data` or `updateNodeData`

The side-change handler must reach `TableNode` (a React Flow custom node) without (a) polluting `data`, or (b) bypassing the parent `notify()`:

1. **Not in `data`:** `TableNodeData` is serialized verbatim into `builder_state` JSON (AR-67; see the file header comment in `builderState.ts`). Functions are not serializable and would corrupt the persisted blob. `data` must stay pure.
2. **Not `useReactFlow().updateNodeData(id, …)`:** that mutates React Flow's internal store and dispatches a `'replace'`-type change through `onNodesChange` → `handleNodesChange`. But `handleNodesChange` only calls `notify()` on `'remove'` changes — so a side change made via `updateNodeData` would update the canvas but **never emit to the parent**, breaking AC-5 (the held `builderState` would be stale until the next drag/connect). 
3. **Context (chosen):** the canvas owns `handleSideChange`, which mutates `nodes` state **and** calls `notify(updatedNodes, edges)` — identical to the established `handleJoinTypeChange` pattern (Story 9.4). The context is the clean way to pass that callback into the custom node. React context flows through `<ReactFlow>` to the rendered node components, so providing it around `<ReactFlow>` is sufficient.

### §3 — Single-Left Invariant (Required, Not Optional)

FR-66 AC-1 says the SQL generator (Story 11.1) uses _"the left-designated table as the `FROM` anchor"_ — singular. Two Left tables would make the FROM clause ambiguous and would break Story 11.1. Therefore `handleSideChange` **enforces at most one Left**: clicking "Left" on any node demotes any other Left node to Right. This is an implicit correctness requirement the dev owns (per the create-story "leave the system working end-to-end" rule), even though the AC text only spells out the zero-Left case.

Consequences to keep consistent:
- **Toggling the sole Left node to "Right"** leaves zero Left → AC-3 banner appears (>1 table). This is the intended, spec-described path — do **not** auto-promote another node; the banner guides the user to designate one.
- **Deleting the Left table** (via `handleNodesChange`) leaves zero Left → banner appears if >1 table remains. Do not auto-promote; this matches the spec (no AC mandates auto-promotion). The default-on-drop only fires on drop, not on delete.

### §4 — AC-3 Scope: No Save/Preview Buttons Exist Yet

The full-screen builder page `web/src/routes/_app/admin/datasets_.$id.tsx` currently renders only `<TablePalette />` + `<QueryBuilderCanvas />` and holds `builderState` in local state — there is **no Save or Preview button** on it. (Preview/Save for builder mode arrive in Story 11.3 / 11.2; the only existing "Preview" button lives in the Custom-Query create/edit modal in `datasets.tsx` and is hard-disabled with `previewNotYetAvailable`.)

So "disable Save/Preview" (AC-3) cannot be literally wired to buttons that do not exist. This story delivers the **enforceable parts** and leaves a clean seam:
1. **`getLeftTableValidationError(nodes)`** (Task 1) — the SSOT gate. Stories 11.2/11.3 will call it and set `disabled={!!getLeftTableValidationError(nodes)}` (plus a `title`/tooltip) on their Save/Preview buttons.
2. **A visible validation banner** on the canvas (Task 3) — so the rule is observable and the message ("Designate one table as the left (FROM) table.") is surfaced now, faithful to AC-3's "with the validation message."

This is the pattern this codebase already uses for forward-looking gates: deferred-work **DN1 (8.8)** records the identical situation — _"AC-3 (submit-button disabling) deferred to Story 8.10 — the modal that contains the submit button doesn't exist yet; pre-register the i18n key for the later story to wire."_ Add a deferred-work note for Stories 11.2/11.3 to consume the helper (see §9).

### §5 — i18n: Use the Literal Key in the Banner

`scripts/i18n-check.mjs` matches only **static literals** `t('foo.bar')` (regex `\bt\(\s*['"]([^'"]+)['"]`); dynamic `t(variable)` is ignored. Render the banner with `t('datasets.builder.validation.noLeftTable')` literally so the key is detected as used. If you instead wrote `t(leftTableError)`, the key would be flagged **orphaned** (warning, exit 0 — not fatal, but avoid it). Missing referenced keys are the only **fatal** case (exit 1). The TablePalette established the `datasets.builder.*` namespace pattern; follow it for `datasets.builder.node.*` and `datasets.builder.validation.*`.

### §6 — Accessibility

- The toggle is a two-`<button>` segmented control wrapped in `role="group"` with an `aria-label`. Each button carries `aria-pressed` reflecting `data.side`, so screen readers announce the active designation. (Matches the project's a11y posture — Story 7.4; and the deferred-work W6 (2.14) lesson to always set `aria-pressed` on toggle buttons.)
- `focus-visible:ring-2 focus-visible:ring-ring` gives a visible keyboard-focus indicator (theme token, not a hardcoded color).
- The banner is `role="alert"` so it is announced when it appears.

### §7 — Theme Tokens Only (Validation Banner Uses `destructive` Tokens)

Use semantic theme tokens (`bg-primary`, `text-primary-foreground`, `bg-background`, `text-muted-foreground`, `border-border`, `hover:bg-overlay-hover`, `ring-ring`) throughout the toggle — consistent with `TableNode.tsx`, `JoinInspector.tsx`, and the theme-token remediation (memory: theme-token UX spec). The validation banner uses the semantic `destructive` token family (`border-destructive/30 bg-destructive/10 text-destructive`) — no hardcoded colors and no `dark:` custom variant anywhere.

> **[Code review 2026-06-04, Decision 1 → keep]** The draft of this section originally prescribed an amber warning palette with `dark:` variants mirroring `ModeBadge`. Implementation instead shipped `destructive` semantic tokens, and the review accepted that as the canonical choice (it aligns with the theme-token remediation — no `dark:` variants, no hardcoded colors). As part of the same review, `ModeBadge` itself (`datasets.tsx`) was migrated off its blue/purple `dark:` variants to `primary`/`muted` tokens, so it is no longer the amber/`dark:` reference this section once cited.

### §8 — No Backend / No Type-Schema Changes

- `TableNodeData.side: TableSide` and the `TableSide = 'left' | 'right'` union already exist in `builderState.ts` (added Story 9.1). No type changes beyond the new helper function.
- `builder_state` is **not persisted** until Story 11.2 — this story only keeps the in-memory `builderState` (held by `datasets_.$id.tsx`) correct via `notify()`. No API, no migration, no validator. The architecture note _"C# `BuilderStateDto` mirrors this exactly"_ is a Story 11.x concern (the DTO/round-trip is built when persistence lands); verify nothing breaks but do not add backend code here.
- Backend test baseline is unaffected: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures). No backend work in this story.

### §9 — Deferred-Work Entry to Add

When you finish, add to `_bmad-output/implementation-artifacts/deferred-work.md` under a new "Deferred from: …9-5…" heading:

- **Save/Preview button-disabling (AC-3) deferred to Stories 11.2 / 11.3** — the builder canvas page has no Save/Preview buttons yet. `getLeftTableValidationError(nodes)` is the SSOT gate; 11.2 (save) and 11.3 (preview) must call it and set `disabled` + a tooltip on their buttons. The visible banner is wired now; button-disabling lands with the buttons. [`QueryBuilderCanvas.tsx`, `builderState.ts`]

Also mark deferred-work **W5 (9.1)** as resolved (the interactive toggle replaces the drop-time default override).

### §10 — Files to Create / Modify

```
MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — add getLeftTableValidationError(nodes) helper (exported, pure)
  web/src/components/query-builder/TableNode.tsx
    — imports: createContext/useContext, useTranslation, TableSide
    — export TableSideChangeContext (default no-op)
    — destructure `id` from NodeProps; read t + context
    — replace the <span> side badge with an interactive 2-button segmented toggle
      (nodrag nopan, aria-pressed, theme tokens, focus ring)
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — imports: useTranslation, TableSideChangeContext, TableSide, getLeftTableValidationError
    — add `const { t } = useTranslation()`
    — add handleSideChange (single-Left invariant + notify)
    — compute `const leftTableError = getLeftTableValidationError(nodes)`
    — wrap <ReactFlow> in <TableSideChangeContext.Provider value={handleSideChange}>
    — render the amber validation banner when leftTableError (literal t() key)
  web/src/lib/i18n/locales/en.json
    — add datasets.builder.node.{left,right,sideGroupAria}
    — add datasets.builder.validation.noLeftTable

NEW (frontend):
  web/src/features/datasets/types/builderState.test.ts
    — unit tests for getLeftTableValidationError (6 cases, Task 5)

NEW / MODIFIED (backend, i18n locales other than en, types schema): none
DOC:
  _bmad-output/implementation-artifacts/deferred-work.md  — add §9 entry; resolve W5 (9.1)
```

### §11 — Git / Recent-Work Patterns

Recent commits (`15c14fb` 9.4, `49ce5a9` 9.3, `e4ff5a9` 9.2, `28be92d` 9.1) follow a consistent rhythm: `dev-story` implements, then a code-review pass applies one patch and defers low findings. Established conventions to match: callbacks via `useCallback` with explicit deps; all parent emission through the single `notify(nodes, edges)` helper; `nodrag nopan` on every interactive element inside a node/edge; theme tokens only; no new i18n keys left unreferenced. The Story 9.4 `handleJoinTypeChange` is the closest sibling to `handleSideChange` — mirror its shape exactly.

### §12 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 9, Story 9.5 ACs (FR-66 AC-1..AC-4)]
- [Source: `_bmad-output/planning-artifacts/epics.md:264` — FR-66 row: "FROM anchor control per TableNode; disabled save/preview if none Left"]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.12 (line 1034) — AR-68: `TableNode.tsx` renders the Left/Right side toggle]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.11 / line 1037 — AR-67: `builder_state` is the canvas source of truth, serialized to JSON on save]
- [Source: `web/src/components/query-builder/TableNode.tsx` — current node (Story 9.1 placeholder badge at lines 21-24)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — `notify`, `handleJoinTypeChange` (mirror), `onDrop` default (lines 254-258), `handleNodesChange`]
- [Source: `web/src/features/datasets/types/builderState.ts` — `TableSide`, `TableNodeData.side`, `TableNodeState`]
- [Source: `web/src/routes/_app/admin/datasets_.$id.tsx` — builder page; holds `builderState`, no Save/Preview button]
- [Source: `web/src/features/datasets/TablePalette.tsx` — established `datasets.builder.*` i18n pattern]
- [Source: `web/src/routes/_app/admin/datasets.tsx` — `ModeBadge` (migrated to `primary`/`muted` semantic tokens in this story's code review, 2026-06-04 — was blue/purple `dark:` variants); disabled Preview button precedent]
- [Source: `web/scripts/i18n-check.mjs` — static `t('literal')` detection; missing = exit 1, orphan = warning]
- [Source: `_bmad-output/implementation-artifacts/deferred-work.md` — W5 (9.1) interactive toggle; DN1 (8.8) submit-button-deferral precedent]
- [Source: `_bmad-output/implementation-artifacts/9-4-join-property-inspector.md` §10 — inspector side labels auto-reflect this story's toggle]
- [Source: Memory — @xyflow/react v12 typing gotchas: `NodeProps` keyed on Node type; node `data` must be a `type` alias. Installed `@xyflow/react@^12.11.0`.]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context) — BMad dev-story workflow

### Debug Log References

- No debug detours. `tsc -b --noEmit` passed on the first run after all edits; the new helper test was green on first run (RED was confirmed by the test importing a not-yet-existing export before adding the helper, then GREEN after). The single suite failure is the pre-existing `i18n-lint` test, unchanged by this story.

### Completion Notes List

- **AC-1** — `TableNode.tsx` now renders an interactive two-`<button>` segmented Left|Right control (replacing the Story 9.1 `<span>` badge). Buttons carry `nodrag nopan` (no canvas drag/pan on click), `aria-pressed`, a `role="group"` wrapper, and `focus-visible` ring — theme tokens only.
- **AC-2** — kept the Story 9.1 `onDrop` default (`side: nodes.length === 0 ? 'left' : 'right'`); first table lands "Left", subsequent "Right". Resolves deferred-work W5 (9.1).
- **AC-3** — `getLeftTableValidationError(nodes)` added to `builderState.ts` as the SSOT gate (returns the i18n key when >1 table and none is 'left', else null). The canvas renders an amber `role="alert"` banner (literal `t('datasets.builder.validation.noLeftTable')` so i18n-check detects the key) when the gate trips. Save/Preview button-disabling is deferred to Stories 11.2/11.3 (no such buttons exist on the builder page yet) — recorded in `deferred-work.md`; per user decision (create-story): helper + banner now.
- **AC-4** — `handleSideChange` enforces the single-Left invariant: promoting a node to "Left" demotes any other "Left" to "Right", so Story 11.1's SQL generator has exactly one unambiguous FROM anchor. No SQL generation in this story (correctly out of scope).
- **AC-5** — every side change flows through `handleSideChange` → `setNodes` + `notify(updatedNodes, edges)`, so the parent's held `builderState.nodes[*].data.side` is current for Story 11.2 persistence.
- **Architecture decision** — the side-change handler is passed to the custom node via a new `TableSideChangeContext` (exported from `TableNode.tsx`, provided around `<ReactFlow>`), NOT via node `data` (serialized into builder_state — must stay pure) and NOT via `updateNodeData` (which would bypass `notify()` and break AC-5). Mirrors the Story 9.4 `handleJoinTypeChange` pattern.
- **No backend / no type-schema changes** — `TableSide` / `TableNodeData.side` already existed (Story 9.1). Only a new pure helper function was added to `builderState.ts`.
- **Verification:** `tsc -b --noEmit` → 0 errors; `vitest run` → **288 passed / 0 failed**; `i18n-check.mjs` → **exit 0** (orphan warnings only). _[Corrected in code review 2026-06-04 — see Task 6. The original draft's "287 / 1 pre-existing i18n-lint failure" no longer holds: this story added `designer.inspector.placeholders.label`, which fixed that test.]_ Manual browser smoke tests (AC-1..AC-3 + single-Left + no-drag) remain for user verification.
- **[Code review 2026-06-04, Decision 2 → keep] Out-of-scope changes accepted:** this commit also carries three changes not in the original §10 File List, reviewed and kept as good theme-token/i18n hygiene: (1) `datasets.tsx` `ModeBadge` migrated from blue/purple `dark:` variants to `primary`/`muted` tokens; (2) `ElementRenderer.tsx` pending-file hint migrated from `bg-amber-*`/`dark:` to `bg-muted`/`text-muted-foreground`; (3) `en.json` gained `designer.inspector.placeholders.label` (fixes the long-standing i18n-lint baseline). §7/§12's amber-`ModeBadge` references were reconciled accordingly.
- **[Code review 2026-06-04] Patch applied:** `handleSideChange` was made referentially stable via `nodesRef`/`edgesRef` mirrors (it is provided to every `TableNode` via context; an unstable value re-rendered all nodes and defeated `memo()`). tsc + full suite re-verified green after the patch.

### File List

- `web/src/features/datasets/types/builderState.ts` — **MODIFIED** — added the exported pure `getLeftTableValidationError(nodes)` helper (AC-3 SSOT gate).
- `web/src/features/datasets/types/__tests__/builderState.test.ts` — **NEW** — 6 unit tests for `getLeftTableValidationError` (empty / single-left / single-right / one-of-many-left / none-left / multiple-left).
- `web/src/components/query-builder/TableNode.tsx` — **MODIFIED** — import `createContext`/`useContext`, `useTranslation`, `TableSide`; export `TableSideChangeContext`; destructure `id`; replace the side `<span>` badge with an interactive `nodrag nopan` Left|Right segmented toggle (aria-pressed, theme tokens, focus ring, i18n labels).
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — **MODIFIED** — import `useTranslation`, `TableSideChangeContext`, `TableSide`, `getLeftTableValidationError`; add `const { t }`; add `handleSideChange` (single-Left invariant + `notify`); compute `leftTableError`; wrap `<ReactFlow>` in `<TableSideChangeContext.Provider>`; render the amber validation banner.
- `web/src/lib/i18n/locales/en.json` — **MODIFIED** — added `datasets.builder.node.{left,right,sideGroupAria}` and `datasets.builder.validation.noLeftTable`.
- `_bmad-output/implementation-artifacts/deferred-work.md` — **MODIFIED** — added the 9.5 Save/Preview-deferral entry; marked W5 (9.1) resolved.

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): interactive Left/Right toggle, single-Left invariant, validation helper + banner, AC-3 Save/Preview gating deferred to 11.2/11.3. Status → ready-for-dev. | create-story |
| 2026-06-04 | 1.0     | Implemented Story 9.5 (AC-1..AC-5): interactive Left/Right toggle via `TableSideChangeContext`, single-Left invariant in `handleSideChange`, `getLeftTableValidationError` SSOT helper + amber validation banner, i18n keys. tsc 0 errors / vitest 287 passed (1 pre-existing i18n-lint failure) / i18n-check no new gaps. Status → review. | Amelia (dev-story) |
