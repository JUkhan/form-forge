# Story 10.1: Per-Table Column Selection

Status: done

## Story

As a user building a Dataset,
I want to check and uncheck columns for each table node,
so that only the relevant columns appear in the SELECT clause.

## Acceptance Criteria

**AC-1 — Column checkboxes are visible and interactive; all start unchecked**
**Given** a table node on the canvas
**When** it renders
**Then** each column row shows an interactive checkbox; all columns start unchecked by default on first drag (user opts in per-column) (per FR-67 J-1 AC-1–AC-2).
**Scope note:** Story 9.1 already renders column checkboxes as `readOnly disabled` placeholders. Story 10.1 removes those attributes and wires the checkbox to a canvas-level handler via a new `ColumnCheckContext` — same pattern as `TableSideChangeContext` from Story 9.5.

**AC-2 — Column selection persists to builder_state**
**Given** I check a column
**When** I save
**Then** the column selection is stored in `builder_state.nodes[*].data.columns[*].checked` (per FR-67 J-1 AC-3).
**Scope note:** `builder_state` is not persisted to the server until Story 11.2. This story keeps the in-memory `builderState` (held by `datasets_.$id.tsx`) correct via `notify()` — Story 11.2 persists it. No API calls, no backend changes.

**AC-3 — No columns selected blocks Save and Preview**
**Given** no column is checked across any table node
**When** I attempt to Save or Preview
**Then** both are blocked with the message: "Select at least one column." (per FR-67 J-1 AC-4).
**Scope note:** Save/Preview buttons do not exist yet (they land in Stories 11.2/11.3). This story delivers: (a) a pure `getColumnSelectionValidationError(nodes)` helper — the SSOT that 11.2/11.3 will call to disable their buttons, and (b) a visible validation banner on the canvas. Same deferral pattern as Story 9.5 AC-3 ("no left table" gate).

---

## Tasks / Subtasks

### Task 1 — Frontend: Add `getColumnSelectionValidationError` to `builderState.ts` (AC-3)

Open `web/src/features/datasets/types/builderState.ts`. After `getLeftTableValidationError` (end of file), add the new pure helper:

```typescript
// Story 10.1: Validation gate for column selection.
// Returns the i18n key when no column is checked across any table node,
// or null when at least one column is checked.
// Single source of truth — the canvas validation banner (Story 10.1) and the
// Save/Preview buttons (Stories 11.2/11.3) both consume this.
//
// Rule (FR-67 J-1 AC-4): zero nodes is valid (no query to build); ≥1 node
// with zero checked columns across all nodes is invalid.
export function getColumnSelectionValidationError(
  nodes: Pick<TableNodeState, 'data'>[],
): 'datasets.builder.validation.noColumnsSelected' | null {
  if (nodes.length === 0) return null
  const hasChecked = nodes.some((n) => n.data.columns.some((c) => c.checked))
  return hasChecked ? null : 'datasets.builder.validation.noColumnsSelected'
}
```

Note: typed against `Pick<TableNodeState, 'data'>[]` so it accepts both `TableNodeState[]` and the canvas's `TableNodeType[]` — identical to `getLeftTableValidationError`.

### Task 2 — Frontend: Export `ColumnCheckContext` from `TableNode.tsx` and make the checkbox interactive (AC-1, AC-2)

Open `web/src/components/query-builder/TableNode.tsx`.

**Step 1 — Update imports.** Add `useContext` (already imported — verify), and import `ColumnSelection` if needed (currently inferred from `TableNodeData.columns`):

```typescript
import { memo, createContext, useContext } from 'react'
```
(Already correct — no import change needed.)

**Step 2 — Export the `ColumnCheckContext` directly after `TableSideChangeContext`:**

```typescript
// Story 10.1: the canvas provides the column-check handler via context so it can
// update a specific column's `checked` flag and emit to the parent via notify().
// Kept out of node `data` because `data` is serialized into builder_state (must
// stay pure — functions are not serializable). Default is a no-op for safety.
export const ColumnCheckContext = createContext<
  (nodeId: string, columnName: string, checked: boolean) => void
>(() => {})
```

**Step 3 — Read the context inside `TableNode` and wire the checkbox.**

After `const onSideChange = useContext(TableSideChangeContext)`, add:
```typescript
const onColumnCheck = useContext(ColumnCheckContext)
```

**Step 4 — Replace the disabled checkbox** in the column list map. Current code (lines 77–83):
```tsx
<input
  type="checkbox"
  checked={col.checked}
  readOnly
  disabled
  className="h-3.5 w-3.5 cursor-not-allowed opacity-60"
/>
```
Replace with:
```tsx
{/* Story 10.1: interactive. `nodrag nopan` prevents checkbox click from
    starting a node drag or canvas pan — same guard as the Left/Right buttons. */}
<input
  type="checkbox"
  checked={col.checked}
  onChange={(e) => onColumnCheck(id, col.columnName, e.target.checked)}
  aria-label={col.columnName}
  className="nodrag nopan h-3.5 w-3.5 cursor-pointer"
/>
```

Remove `readOnly`, `disabled`, `cursor-not-allowed`, `opacity-60`. Add `nodrag nopan`, `cursor-pointer`, `onChange`, and `aria-label`. Using `col.columnName` as the `aria-label` keeps it accessible without adding an i18n key (column names are database identifiers, already in English).

Leave the rest of the node (header, handles, pgType span, selected ring) **unchanged**.

### Task 3 — Frontend: Wire `handleColumnCheck` + validation banner in `QueryBuilderCanvas.tsx` (AC-1, AC-2, AC-3)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Update imports.** Add `ColumnCheckContext` to the existing `TableNode` import:

```typescript
import {
  TableNode,
  TableSideChangeContext,
  ColumnCheckContext,
  type TableNodeType,
} from '../../components/query-builder/TableNode'
```

Add `getColumnSelectionValidationError` to the existing `builderState` import:

```typescript
import {
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  type BuilderState,
  type ColumnSelection,
  type JoinEdgeState,
  type JoinType,
  type TableNodeData,
  type TableNodeState,
  type TableSide,
} from './types/builderState'
```

**Step 2 — Add `handleColumnCheck` in `QueryBuilderCanvasInner`.** Place it near `handleSideChange`. Reads from `nodesRef`/`edgesRef` (already present from Story 9.5 code review patch) so its identity is stable — identical posture to `handleSideChange`:

```typescript
// Story 10.1 AC-1/AC-2: toggle one column's `checked` flag and emit via notify().
// Reads refs (not closure state) to stay referentially stable — same posture as
// handleSideChange, so the ColumnCheckContext.Provider value doesn't change every
// render and defeat TableNode's memo().
const handleColumnCheck = useCallback(
  (nodeId: string, columnName: string, checked: boolean) => {
    const updatedNodes = nodesRef.current.map((n) => {
      if (n.id !== nodeId) return n
      return {
        ...n,
        data: {
          ...n.data,
          columns: n.data.columns.map((c) =>
            c.columnName === columnName ? { ...c, checked } : c,
          ),
        },
      }
    })
    setNodes(updatedNodes)
    notify(updatedNodes, edgesRef.current)
  },
  [setNodes, notify],
)
```

**Step 3 — Compute both validation errors** (place near `leftTableError`):

```typescript
const leftTableError = getLeftTableValidationError(nodes)
const columnSelectionError = getColumnSelectionValidationError(nodes)
```

**Step 4 — Provide the new context alongside `TableSideChangeContext.Provider`.** Nest it inside:

```tsx
<TableSideChangeContext.Provider value={handleSideChange}>
  <ColumnCheckContext.Provider value={handleColumnCheck}>
    <ReactFlow
      ...
    >
      <Background />
      <Controls />
      <MiniMap />
    </ReactFlow>
  </ColumnCheckContext.Provider>
</TableSideChangeContext.Provider>
```

**Step 5 — Refactor the validation banner area.** Replace the current single `{leftTableError && (...)}` block with a stacked container that handles both errors:

```tsx
{(leftTableError || columnSelectionError) && (
  <div className="nodrag nopan pointer-events-none absolute left-1/2 top-4 z-40 flex -translate-x-1/2 flex-col items-center gap-1">
    {leftTableError && (
      <div
        role="alert"
        className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
      >
        {t('datasets.builder.validation.noLeftTable')}
      </div>
    )}
    {columnSelectionError && (
      <div
        role="alert"
        className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
      >
        {t('datasets.builder.validation.noColumnsSelected')}
      </div>
    )}
  </div>
)}
```

**Critical:** render the banner text with the **literal** `t('datasets.builder.validation.noColumnsSelected')` (not `t(columnSelectionError)`), so the i18n-check script statically detects the key as used. Same rule as `t('datasets.builder.validation.noLeftTable')` in Story 9.5 §5.

The `{selectedEdge && <JoinInspector ... />}` block stays unchanged after the banners.

### Task 4 — Frontend: Add i18n key to `en.json` (AC-3)

Open `web/src/lib/i18n/locales/en.json`. Extend the existing `datasets.builder.validation` object:

```json
"validation": {
  "noLeftTable": "Designate one table as the left (FROM) table.",
  "noColumnsSelected": "Select at least one column."
}
```

The key is referenced by the literal `t('datasets.builder.validation.noColumnsSelected')` in Task 3 Step 5. Adding a key without a matching `t()` literal → orphan warning (non-fatal); adding a `t()` literal without the key → **exit 1 (fatal)**. Both must be present.

### Task 5 — Frontend: Unit tests for `getColumnSelectionValidationError` (AC-3)

Add to the existing `web/src/features/datasets/types/__tests__/builderState.test.ts` (which already tests `getLeftTableValidationError` from Story 9.5). Import `getColumnSelectionValidationError` and add a new `describe` block:

Cases (build minimal node shapes inline — see existing test for the shape pattern):
- `[]` → `null` (zero nodes — nothing to validate)
- 1 node, 0 columns → `null` (no columns at all — degenerate node, treat as valid; actual column data comes from the catalog)
- 1 node, 1 column unchecked → `'datasets.builder.validation.noColumnsSelected'`
- 1 node, 1 column checked → `null`
- 2 nodes, all columns unchecked → `'datasets.builder.validation.noColumnsSelected'`
- 2 nodes, one column checked on the second node → `null` (any checked column satisfies the gate)

Minimal column shape: `{ columnName: 'id', pgType: 'uuid', checked: false, aggregate: 'none', alias: '' }`.

### Task 6 — Frontend: Add deferred-work entry (AC-3 deferral to 11.2/11.3)

Add to `_bmad-output/implementation-artifacts/deferred-work.md` under a new heading:

```markdown
## Deferred from: implementation of 10-1-per-table-column-selection

- **Save/Preview button-disabling (AC-3) deferred to Stories 11.2 / 11.3** — the builder canvas page has no Save/Preview buttons yet. `getColumnSelectionValidationError(nodes)` in `builderState.ts` is the SSOT gate; 11.2 (save) and 11.3 (preview) must call it alongside `getLeftTableValidationError(nodes)` and set `disabled` + a tooltip on their buttons. The visible banner is wired now; button-disabling lands with the buttons. [`QueryBuilderCanvas.tsx`, `types/builderState.ts`]
```

### Task 7 — Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors ✅ (TSC_EXIT=0)
- [x] `cd web && npx vitest run` → 294 passed / 0 failed ✅ (288 baseline + 6 new column-selection tests; 27 files passed)
- [x] `cd web && node scripts/i18n-check.mjs` → exit 0 ✅ (`datasets.builder.validation.noColumnsSelected` is NOT in the orphan list → referenced by the literal `t()`; only pre-existing orphans warn, non-fatal)
- [ ] Manual smoke (AC-1): open a Query Builder dataset; drag one table; confirm all column checkboxes are now clickable (not greyed out); click one — it becomes checked; click again — unchecked. _(Not executable in this headless env — see Completion Notes; covered by code inspection + automated gates.)_
- [ ] Manual smoke (AC-2): check a column on one node and drag another node; confirm both nodes retain their checked states independently. Checking a column on one node does not affect other nodes. _(Manual browser check — see Completion Notes.)_
- [ ] Manual smoke (AC-3, no-columns): with ≥1 table on canvas and no columns checked, confirm the "Select at least one column." destructive banner appears at top-center. Check one column — confirm the banner disappears. With zero tables on canvas, confirm no column-selection banner. _(Manual browser check — gate logic unit-tested in builderState.test.ts.)_
- [ ] Manual smoke (both banners): with >1 table and no Left designated AND no columns checked, confirm both banners ("Designate one table…" + "Select at least one column.") stack vertically. _(Manual browser check.)_
- [ ] Manual smoke (no-drag): clicking a checkbox does **not** drag the node or pan the canvas (`nodrag nopan` works). _(Manual browser check.)_
- [ ] Manual smoke (inline): confirm the column row renders correctly with the checkbox, column name, and pgType still visible and aligned. _(Manual browser check.)_

### Review Findings (Code Review 2026-06-04)

Adversarial review (Blind Hunter + Edge Case Hunter + Acceptance Auditor). Outcome: **0 decision-needed, 0 patch, 3 deferred, 8 dismissed.** Acceptance Auditor confirmed all three ACs satisfied with no defects; the only spec divergence is the user-approved 0-column gate decision.

Deferred (no in-scope fix; routed to the right future home):

- [x] [Review][Defer] Column identity keyed on `columnName` — duplicate names within one node would toggle/render together [`TableNode.tsx:78,84,91`, `QueryBuilderCanvas.tsx:~213`] — deferred. The checkbox `key`, `Handle id`, and `handleColumnCheck` match all key on `col.columnName`. If a node ever held two same-named columns, both would flip at once and React keys/Handle ids would collide. Unreachable via the catalog (PostgreSQL guarantees per-table column-name uniqueness) and the render pattern is pre-existing (9.1/9.3); only a legacy/hand-edited `builder_state` blob could trigger it. Belongs in the existing `parseBuilderState` node-shape validation deferral (Story 9.5 W2). (blind+edge)
- [x] [Review][Defer] `data.columns` not defensively defaulted in helper + handler [`builderState.ts:157`, `QueryBuilderCanvas.tsx:~210`] — deferred, same theme as 9.5 W2. `n.data.columns.some(...)` / `.map(...)` assume `columns` is always an array; a malformed persisted blob with a missing `columns` field would throw. Add a node-shape guard / `?? []` when `parseBuilderState` validation lands with persistence (11.2). (blind)
- [x] [Review][Defer] `getColumnSelectionValidationError` is satisfied by any checked column on any node — does not require the LEFT/FROM table to have one [`builderState.ts:153-159`] — deferred as a note for Story 11.1. Spec-conformant for 10.1 (FR-67 J-1 AC-4: "≥1 checked column anywhere"); whether the FROM anchor specifically needs a selected column is a SQL-generation concern for Story 11.1. (blind)

Dismissed (8): stale-ref/controlled-checkbox concern (false positive — `nodesRef`/`edgesRef` synced every render at `QueryBuilderCanvas.tsx:178,180`); dangling edge when unchecking a joined column (valid SQL — ON-clause columns need not be SELECTed); no read-only/preview gate on the checkbox (no read-only builder context exists; consistent with the Left/Right buttons); `aria-label` lacks table context (spec §8 prescribes `col.columnName`); two simultaneous `role="alert"` banners (spec Task 3 §5 prescribes per-banner `role="alert"`; rare co-occurrence); `onColumnCheck` no-op default (by-design, mirrors `TableSideChangeContext`); empty-canvas exemption needing both gates (already documented in the deferred-work entry); 0-column-node gate returning the key (intentional, user-approved, matches the test).

---

## Dev Notes

### §1 — Current State Left by Story 9.5

`TableNode.tsx:77–83` renders column checkboxes with `readOnly disabled cursor-not-allowed opacity-60` — explicit placeholder pending this story. The comment on line 66 (`"Column list — checkboxes non-interactive until Story 10.1"`) confirms this is the handoff point.

The `ColumnSelection` type in `builderState.ts:14–20` already has `checked: boolean` (comment: "Epic 10: Story 10.1"), `aggregate: AggregateFunction`, and `alias: string` — these are the fields Epic 10 fills in across Stories 10.1–10.2. This story uses only `checked`; `aggregate` and `alias` are populated in Story 10.2 (they already initialize to `'none'` / `''` in `onDrop`).

### §2 — Why a Context, Not Node `data` or `updateNodeData`

Identical rationale to Story 9.5 §2: `data` is serialized verbatim into `builder_state` JSON (AR-67); functions are not serializable. `useReactFlow().updateNodeData()` mutates React Flow's internal store but bypasses the parent `notify()`, breaking AC-2. The context pattern (owned by the canvas, provided around `<ReactFlow>`) is the established approach — `TableSideChangeContext` is the exact sibling to mirror.

### §3 — Stable Handler Identity (nodesRef/edgesRef Pattern)

`handleColumnCheck` is provided to every `TableNode` via context. If it changed identity each render (closing over `nodes`/`edges`), the `ColumnCheckContext.Provider value` would change every render and re-render all nodes, defeating `TableNode`'s `memo()`. Reading from `nodesRef.current` / `edgesRef.current` (mirrors already added in Story 9.5 code review patch to `QueryBuilderCanvas.tsx:175–178`) lets the callback stay stable. Deps: `[setNodes, notify]` — same as `handleSideChange`.

Do **not** add `nodes`/`edges` to `handleColumnCheck`'s deps. Do **not** add `handleColumnCheck` to `onNodeDragStop`'s or any other callback's deps.

### §4 — AC-3 Scope: No Save/Preview Buttons Exist Yet

Same situation as Story 9.5 §4. The builder page (`datasets_.$id.tsx`) has no Save or Preview button. This story delivers:
1. `getColumnSelectionValidationError(nodes)` — the SSOT gate (Task 1). Stories 11.2/11.3 call it and set `disabled={!!getColumnSelectionValidationError(nodes)}` (and the left-table gate) on their buttons.
2. A visible validation banner (Task 3) — "Select at least one column." — observable now.

Add a deferred-work entry (Task 6) so Stories 11.2/11.3 don't miss consuming **both** validation helpers.

### §5 — i18n: Use Literal Keys in the Banners

`scripts/i18n-check.mjs` detects only static `t('foo.bar')` literals (regex `\bt\(\s*['"]([^'"]+)['"]`). Render the banner with:
- `t('datasets.builder.validation.noLeftTable')` (unchanged from 9.5)
- `t('datasets.builder.validation.noColumnsSelected')` (new in 10.1)

Never use `t(columnSelectionError)` — the key would be flagged orphaned (warning, exit 0), but the literal `t()` call would be undetected and might trigger an exit 1 if the literal is later required by other tooling. Keep clean: literal key in `t()` call, key present in `en.json`.

### §6 — Banner Refactoring

Story 9.5 rendered a single `{leftTableError && (<div role="alert">...)}` block. Story 10.1 refactors this into a stacked container so both banners can coexist:

```tsx
{(leftTableError || columnSelectionError) && (
  <div className="nodrag nopan pointer-events-none absolute left-1/2 top-4 z-40 flex -translate-x-1/2 flex-col items-center gap-1">
    {leftTableError && <div role="alert" ...>...</div>}
    {columnSelectionError && <div role="alert" ...>...</div>}
  </div>
)}
```

This is a refactor of the Story 9.5 banner output — same classes on the inner divs, new outer container. The `relative` class on the `reactFlowWrapper` div stays (already there from Story 9.4, reused by Story 9.5 for JoinInspector positioning).

### §7 — No Backend / No Type-Schema Changes

- `ColumnSelection.checked` already exists in `builderState.ts`.
- `builder_state` is not persisted until Story 11.2 — this story only keeps the in-memory `builderState` (held by `datasets_.$id.tsx`) correct via `notify()`.
- No API calls, no migration, no `BuilderStateDto` changes. The C# DTO update (AR-67 note: "C# `BuilderStateDto` mirrors this exactly") happens in Epic 11 stories when persistence lands.
- Backend test baseline: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures). No backend work in this story.

### §8 — Accessibility

- The checkbox gets `aria-label={col.columnName}` — using the column name directly avoids an i18n key (column names are database identifiers). Screen readers announce "id, checkbox, not checked" etc.
- `nodrag nopan` is required on the checkbox: without it, mousedown on the checkbox initiates a node drag in React Flow (same reason the Left/Right buttons needed it in Story 9.5).
- The validation banner carries `role="alert"` (unchanged from 9.5 pattern) — announced by screen readers when it appears.

### §9 — Deferred-Work Entry to Add

When finished, add to `_bmad-output/implementation-artifacts/deferred-work.md` (Task 6). The key consumer reminder: Stories 11.2/11.3 must call **both** `getLeftTableValidationError(nodes)` AND `getColumnSelectionValidationError(nodes)` to disable Save/Preview buttons. Neither gate alone is sufficient.

### §10 — Files to Create / Modify

```
MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — add getColumnSelectionValidationError(nodes) helper (exported, pure)
  web/src/features/datasets/types/__tests__/builderState.test.ts
    — add describe block for getColumnSelectionValidationError (6 cases, Task 5)
  web/src/components/query-builder/TableNode.tsx
    — export ColumnCheckContext (default no-op)
    — add `const onColumnCheck = useContext(ColumnCheckContext)`
    — make column checkbox interactive: remove readOnly/disabled/cursor-not-allowed/opacity-60,
      add onChange, aria-label, cursor-pointer, nodrag nopan
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — imports: ColumnCheckContext, getColumnSelectionValidationError
    — add handleColumnCheck (stable via nodesRef/edgesRef, deps [setNodes, notify])
    — compute columnSelectionError = getColumnSelectionValidationError(nodes)
    — nest ColumnCheckContext.Provider inside TableSideChangeContext.Provider around ReactFlow
    — refactor banner area: replace single leftTableError block with stacked container
  web/src/lib/i18n/locales/en.json
    — add datasets.builder.validation.noColumnsSelected

NEW / MODIFIED (backend, i18n locales other than en, types schema): none
DOC:
  _bmad-output/implementation-artifacts/deferred-work.md
    — add 10-1 Save/Preview button-disabling deferral entry (Task 6)
```

### §11 — Git / Recent-Work Patterns

Recent commits (85cce0f 9.5, 15c14fb 9.4, 49ce5a9 9.3, e4ff5a9 9.2) follow: `dev-story` implements, then a code-review pass applies one patch and defers low findings. Established conventions:
- Callbacks passed to custom nodes/edges use the `useContext` + stable-ref pattern (see `handleSideChange` / `TableSideChangeContext`).
- All parent emission through the single `notify(nodes, edges)` helper.
- `nodrag nopan` on every interactive element inside a node/edge.
- Theme tokens only; no hardcoded colors, no `dark:` custom variants.
- No new i18n keys left unreferenced.
- `handleColumnCheck` should mirror `handleSideChange` shape exactly (same deps, same ref reads, same `setNodes` + `notify()` pattern).

### §12 — References

- [Source: `_bmad-output/planning-artifacts/epics.md:2605–2623` — Story 10.1 ACs (FR-67 J-1 AC-1..AC-4)]
- [Source: `_bmad-output/planning-artifacts/epics.md:265` — FR-67 row: "Column checkboxes; aggregate/alias; CASE columns; calculated columns; save gated on ≥1 column"]
- [Source: `_bmad-output/planning-artifacts/epics.md:173` — AR-67: builder_state canonical TS interface; C# BuilderStateDto mirrors exactly]
- [Source: `_bmad-output/planning-artifacts/epics.md:174` — AR-68: TableNode.tsx column list, aggregate dropdown, alias input (Epic 10 scope)]
- [Source: `web/src/components/query-builder/TableNode.tsx:66–94` — current disabled-checkbox column list (this story makes it interactive)]
- [Source: `web/src/features/datasets/types/builderState.ts:14–20` — ColumnSelection type (checked, aggregate, alias already defined)]
- [Source: `web/src/features/datasets/types/builderState.ts:127–143` — getLeftTableValidationError (reference pattern for getColumnSelectionValidationError)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx:175–198` — nodesRef/edgesRef mirrors + handleSideChange (reference pattern for handleColumnCheck)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx:325–361` — leftTableError banner (this story refactors into stacked container)]
- [Source: `web/src/lib/i18n/locales/en.json:datasets.builder.validation` — existing validation namespace to extend]
- [Source: `_bmad-output/implementation-artifacts/9-5-table-left-right-designation.md §2,§3,§4,§5,§9` — context pattern rationale, single-source gate pattern, deferral pattern]
- [Source: `_bmad-output/implementation-artifacts/deferred-work.md:1–7` — existing 9.5 deferral entry (model for 10.1 entry)]
- [Source: Memory — @xyflow/react v12 typing gotchas: NodeProps keyed on Node type; node `data` must be a `type` alias]
- [Source: Memory — i18n-lint failure RESOLVED 2026-06-04; web suite now 288 passed/0 failed; a red i18n-lint is now a real regression]

---

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 — BMad create-story workflow
claude-opus-4-8 — BMad dev-story workflow (implementation)

### Debug Log References

- **Spec contradiction resolved (user decision):** Task 1's helper code and Task 5's test list disagreed on the degenerate "node with zero columns" case. Task 1 (verbatim) treats any node-set with zero checked columns as invalid → returns the key; Task 5 expected `null` ("treat as valid"). Difference only manifests for a 0-column node (never produced by the catalog). User chose **Treat as INVALID** — keep the Task 1 helper verbatim and adjust the single conflicting test to expect the key. Rationale: a `SELECT` with no columns is not a valid query, and matches AC-3's literal "no column checked across any table node → blocked."
- Vitest invoked from repo root initially used a freshly-installed vitest@4.1.8 with default config; re-ran from `web/` (project toolchain, vitest 4.1.7) for the authoritative 294-test result.

### Completion Notes List

- **AC-1 (interactive checkboxes):** `TableNode.tsx` checkbox now reads `ColumnCheckContext` and fires `onColumnCheck(id, col.columnName, checked)` on change; removed `readOnly`/`disabled`/`cursor-not-allowed`/`opacity-60`; added `nodrag nopan`, `cursor-pointer`, and `aria-label={col.columnName}`. Columns still start unchecked (the on-drop default `checked: false` in `QueryBuilderCanvas.onDrop` is unchanged).
- **AC-2 (persists to builder_state):** `handleColumnCheck` in `QueryBuilderCanvas.tsx` toggles one column's `checked` flag immutably and emits via `notify(updatedNodes, edgesRef.current)`. Reads `nodesRef`/`edgesRef` (not closure state) with deps `[setNodes, notify]`, so the `ColumnCheckContext.Provider` value stays referentially stable and does not defeat `TableNode`'s `memo()` — same posture as `handleSideChange`. Per-node independence follows from keying the update on `n.id`. (Server persistence remains deferred to Story 11.2; this story keeps the in-memory `builderState` correct.)
- **AC-3 (gate + banner):** Added the pure SSOT helper `getColumnSelectionValidationError(nodes)` to `builderState.ts` (returns the i18n key when no column is checked across any node; `null` for an empty canvas). The banner area was refactored from a single `leftTableError` block into a stacked, top-center container that renders the left-table banner and/or the new "Select at least one column." banner (both `role="alert"`, literal `t()` keys for i18n-check). Save/Preview button-disabling is deferred to Stories 11.2/11.3 (no such buttons exist yet) — logged in deferred-work.md.
- **Manual smoke tests:** the browser-interaction smoke checks in Task 7 were not executable in this headless environment. Implementation matches the spec exactly, the gate logic is covered by 6 new unit tests, and all automated gates (tsc, full vitest suite, i18n-check) pass. Recommend the reviewer confirm the manual smokes in a running app.
- No backend, migration, DTO, or non-en locale changes (per Dev Notes §7).

### File List

- `web/src/features/datasets/types/builderState.ts` — added `getColumnSelectionValidationError` (exported, pure SSOT helper)
- `web/src/features/datasets/types/__tests__/builderState.test.ts` — added `getColumnSelectionValidationError` describe block (6 cases) + `col`/`nodeWithColumns` helpers and import
- `web/src/components/query-builder/TableNode.tsx` — exported `ColumnCheckContext`; read it via `useContext`; made the column checkbox interactive (onChange, aria-label, cursor-pointer, nodrag nopan; removed readOnly/disabled); refreshed two stale comments
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — imported `ColumnCheckContext` + `getColumnSelectionValidationError`; added stable `handleColumnCheck`; computed `columnSelectionError`; nested `ColumnCheckContext.Provider` inside `TableSideChangeContext.Provider`; refactored the banner into a stacked container handling both errors
- `web/src/lib/i18n/locales/en.json` — added `datasets.builder.validation.noColumnsSelected`
- `_bmad-output/implementation-artifacts/deferred-work.md` — added 10-1 Save/Preview button-disabling deferral entry
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — status 10-1 ready-for-dev → in-progress → review
- `_bmad-output/implementation-artifacts/10-1-per-table-column-selection.md` — task/verification checkboxes, Dev Agent Record, File List, Change Log, Status

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): interactive column checkboxes via ColumnCheckContext, getColumnSelectionValidationError SSOT helper + stacked validation banner, AC-3 Save/Preview gating deferred to 11.2/11.3. Status → ready-for-dev. | create-story |
| 2026-06-04 | 0.2     | Implemented (dev-story): ColumnCheckContext + interactive checkbox (AC-1), stable handleColumnCheck emitting via notify (AC-2), getColumnSelectionValidationError SSOT + stacked banner (AC-3), en.json key, deferred-work entry. Resolved Task1/Task5 0-column-node contradiction per user decision (treat as invalid). tsc 0 errors, vitest 294/294, i18n-check exit 0. Status → review. | dev-story |
