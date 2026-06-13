# Story 10.8: Order By Panel

Status: done

## Story

As a user building a Dataset,
I want to define ORDER BY clauses as table + column + direction,
So that the dataset results are sorted in a predictable order.

## Acceptance Criteria

1. Given the canvas toolbar, when I open the "Order By" panel, then I see the current list of ORDER BY clauses (per FR-69 J-8 AC-1)

2. Given an ORDER BY clause, when I configure it, then it shows: table selector (tables on canvas) + column selector + direction toggle (ASC / DESC) (per FR-69 J-8 AC-2)

3. Given multiple clauses in the ORDER BY panel, when I reorder them, then clause order maps directly to SQL ORDER BY precedence — first clause = primary sort (per FR-69 J-8 AC-3)

4. Given the SQL generator runs (Story 11.1), when ORDER BY clauses are configured, then it emits `ORDER BY "table"."col" ASC|DESC, …` in declared clause order — this AC is Story 11.1's responsibility; Story 10.8 only ensures the `orderBy[]` array is correctly populated for the generator to consume (per FR-69 J-8 AC-4)

5. Given the ORDER BY panel is empty, when the SQL generator runs, then no ORDER BY clause is emitted in generated SQL — empty is valid (per FR-69 J-8 AC-5)

6. Given I save, when the save completes, then ORDER BY state is persisted in `builder_state.orderBy[]` and restored on reopen (per AR-67) — persistence is Story 11.2's responsibility; Story 10.8 ensures the state flows through `onChange` correctly so 11.2 can serialize it

## Tasks / Subtasks

- [x] Task 1: Add `OrderByPanel.tsx` component (AC: 1, 2, 3, 5)
  - [x] Create `web/src/components/query-builder/OrderByPanel.tsx` (NEW file)
  - [x] Props interface: `{ isOpen: boolean; orderBy: OrderByClause[]; nodes: TableNodeType[]; onSave: (updated: OrderByClause[]) => void; onClose: () => void }`
  - [x] Local state: `const [localOrderBy, setLocalOrderBy] = useState<OrderByClause[]>(orderBy)` — sync on `isOpen` change with `useEffect([isOpen])`; same pattern as `FilterConditionsDialog.tsx` line 358-362
  - [x] Export 4 pure array-mutation helpers at module scope (before the component) — these are the only new logic and must be exported for unit testing (Task 3):
    - `export function addOrderByClause(clauses: OrderByClause[], defaultTable: string, defaultColumn: string): OrderByClause[]` — appends `{ tableName: defaultTable, columnName: defaultColumn, direction: 'ASC' }`
    - `export function removeOrderByClause(clauses: OrderByClause[], idx: number): OrderByClause[]` — returns `clauses.filter((_, i) => i !== idx)`
    - `export function moveOrderByClause(clauses: OrderByClause[], idx: number, direction: 'up' | 'down'): OrderByClause[]` — swaps with previous (up) or next (down); returns unchanged array (re-cloned) if at boundary
    - `export function updateOrderByClause(clauses: OrderByClause[], idx: number, patch: Partial<OrderByClause>): OrderByClause[]` — returns `clauses.map((c, i) => i === idx ? { ...c, ...patch } : c)`
  - [x] Use `Dialog` from `@/components/ui/dialog` (same import pattern as `FilterConditionsDialog.tsx` lines 1-10)
  - [x] Dialog title: `t('datasets.builder.orderByPanel.title')`
  - [x] "Add Clause" button: calls `setLocalOrderBy(addOrderByClause(localOrderBy, firstNode?.data.tableName ?? '', firstColumn ?? ''))` — derive `firstNode` from `nodes[0]` and `firstColumn` from `nodes[0]?.data.columns[0]?.columnName ?? ''`; disable button when `nodes.length === 0`
  - [x] Per-clause row layout (same `flex items-center gap-1.5` approach as `FilterConditionRow.tsx`):
    - Table `<select>`: options from `nodes.map(n => n.data.tableName)`; on change → `updateOrderByClause(..., { tableName, columnName: firstColForTable })` to reset column when table changes
    - Column `<select>`: options from the selected table's `data.columns`; disabled when table has no columns
    - Direction `<select>` or toggle: options `ASC` / `DESC` (use `<select>` for consistency — same `SELECT_CLASS` from FilterConditionRow)
    - Move-up button: `ChevronUp` icon; disabled at index 0; calls `moveOrderByClause(localOrderBy, idx, 'up')`
    - Move-down button: `ChevronDown` icon; disabled at last index; calls `moveOrderByClause(localOrderBy, idx, 'down')`
    - Delete button: `X` icon; calls `removeOrderByClause(localOrderBy, idx)`; `hover:text-destructive` class
  - [x] Empty state message when `localOrderBy.length === 0`: `t('datasets.builder.orderByPanel.emptyState')`
  - [x] "Apply" button: calls `onSave(localOrderBy)` then `onClose()`; "Cancel" button: calls `onClose()` (discards local changes — same Apply/Cancel pattern as `FilterConditionsDialog.tsx` lines 374-391)
  - [x] Use `SELECT_CLASS` / `INPUT_CLASS` constants matching `FilterConditionRow.tsx` (copy the same constants — do NOT import them across files; co-locate)
  - [x] Import `ChevronUp`, `ChevronDown`, `X` from `'lucide-react'`
  - [x] Import `OrderByClause` type from `'../../features/datasets/types/builderState'`
  - [x] Import `TableNodeType` type from `'./TableNode'`

- [x] Task 2: Wire state and panel into `QueryBuilderCanvas.tsx` (AC: 1, 3, 6)
  - [x] Add `OrderByClause` to the import from `'./types/builderState'` (line 44-56) — it is already defined in `builderState.ts` line 134; just add to the import list
  - [x] Add `OrderByPanel` import: `import { OrderByPanel } from '../../components/query-builder/OrderByPanel'`
  - [x] Add `orderBy` state + ref immediately after the `filters` state block (after line 130):
    ```
    const [orderBy, setOrderBy] = useState<OrderByClause[]>(initialState.orderBy)
    const orderByRef = useRef(orderBy)
    orderByRef.current = orderBy
    ```
  - [x] Add `isOrderByPanelOpen` state after `isFilterDialogOpen` (after line 130):
    ```
    const [isOrderByPanelOpen, setIsOrderByPanelOpen] = useState(false)
    ```
  - [x] Add `handleUpdateOrderBy` callback immediately after `handleSaveFilters` (after line 457) — same ordering: update `extraStateRef.current` BEFORE calling `notify()`:
    ```typescript
    const handleUpdateOrderBy = useCallback(
      (newOrderBy: OrderByClause[]) => {
        setOrderBy(newOrderBy)
        orderByRef.current = newOrderBy
        extraStateRef.current = { ...extraStateRef.current, orderBy: newOrderBy }
        notify(nodesRef.current, edgesRef.current)
      },
      [notify],
    )
    ```
  - [x] Update the toolbar `<div>` (line 648-657): add "Order By" button after the "Filter" button, inside the same `flex items-center gap-2` container:
    ```tsx
    <button
      type="button"
      onClick={() => setIsOrderByPanelOpen(true)}
      aria-label={t('datasets.builder.orderByPanel.openButtonAria')}
      className="rounded-md border border-border bg-card px-3 py-1.5 text-xs font-medium text-foreground shadow-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {t('datasets.builder.orderByPanel.openButton')}
    </button>
    ```
  - [x] Add `<OrderByPanel>` JSX immediately after `<FilterConditionsDialog>` (after line 775):
    ```tsx
    <OrderByPanel
      isOpen={isOrderByPanelOpen}
      orderBy={orderBy}
      nodes={nodes}
      onSave={handleUpdateOrderBy}
      onClose={() => setIsOrderByPanelOpen(false)}
    />
    ```

- [x] Task 3: Add i18n keys to `en.json` (AC: 1, 2)
  - [x] Add `datasets.builder.orderByPanel` object to `web/src/lib/i18n/locales/en.json` inside the `datasets.builder` block, after the `filterDialog` block:
    ```json
    "orderByPanel": {
      "openButton": "Order By",
      "openButtonAria": "Open order by panel",
      "title": "Order By",
      "saveButton": "Apply",
      "addButton": "+ Add Clause",
      "emptyState": "No ORDER BY clauses. Click + Add Clause to start.",
      "tableLabel": "Table",
      "columnLabel": "Column",
      "directionLabel": "Direction",
      "directionAsc": "ASC",
      "directionDesc": "DESC",
      "deleteClauseAria": "Delete clause",
      "moveUpAria": "Move up",
      "moveDownAria": "Move down",
      "noColumnsOption": "No columns",
      "noTablesDisabled": "No tables on canvas"
    }
    ```

- [x] Task 4: Unit tests for the 4 array-mutation helpers (AC: 3, 5)
  - [x] Create `web/src/components/query-builder/__tests__/orderByUtils.test.ts` (NEW file — directory already exists from Story 10.7)
  - [x] Imports: `{ describe, it, expect }` from `'vitest'`; the 4 exported helpers from `'../OrderByPanel'`; `OrderByClause` type from `'../../../features/datasets/types/builderState'`
  - [x] Factory helper: `function makeClause(table: string, col: string, dir: 'ASC' | 'DESC' = 'ASC'): OrderByClause`
  - [x] `addOrderByClause` tests:
    - Appends clause with correct table/column/direction to empty array
    - Appends to non-empty array without mutating the original
  - [x] `removeOrderByClause` tests:
    - Removes clause at index 0 from a 3-element array
    - Removes clause at last index
    - Removes clause at middle index; surrounding clauses intact
  - [x] `moveOrderByClause` tests:
    - Moves item UP: element at index 1 moves to index 0; other elements preserved
    - Moves item DOWN: element at index 0 moves to index 1; other elements preserved
    - No-op at upper boundary: `moveOrderByClause(clauses, 0, 'up')` returns array with same content
    - No-op at lower boundary: `moveOrderByClause(clauses, last, 'down')` returns array with same content
  - [x] `updateOrderByClause` tests:
    - Updates direction only: `{ direction: 'DESC' }` patch; other fields unchanged
    - Updates table and column together; direction unchanged
    - Index out of range: returns unchanged array (edge case — if idx is beyond bounds, `clauses.map` ignores it naturally)

- [x] Task 5: Verify — `npx tsc -b --noEmit` 0 errors; `npm run test` all pass (baseline 344 from Story 10.7; expect ~352+ after new tests); `npm run lint:i18n` exits 0

## Dev Notes

### 1. What Already Exists — Do NOT Recreate

`builderState.ts` already defines everything needed for this story:

- **`OrderByClause` interface** (`builderState.ts` lines 134-138): `{ tableName: string; columnName: string; direction: 'ASC' | 'DESC' }` — no changes to this type
- **`BuilderState.orderBy`** (`builderState.ts` line 172): `orderBy: OrderByClause[]` — already part of the interface
- **`EMPTY_BUILDER_STATE.orderBy`** (`builderState.ts` line 183): `orderBy: []` — already initialized
- **`extraStateRef`** in `QueryBuilderCanvas.tsx` (lines 96-101): already includes `orderBy: initialState.orderBy` in its initial value — the field is already wired at the ref level; Story 10.8 only adds the `useState` mirror and the handler

Do NOT modify `builderState.ts`. Do NOT add validation helpers there (empty `orderBy[]` is explicitly valid per AC-5).

### 2. State Management Pattern (exact copy of filters pattern)

`QueryBuilderCanvas.tsx` uses a strict ref+state pairing for non-node/edge state. The filters pattern (lines 123-130) must be copied verbatim for orderBy:

```typescript
// EXISTING filters pattern (lines 123-130) — copy this exact structure:
const [filters, setFilters] = useState<FilterGroup>(initialState.filters)
const filtersRef = useRef(filters)
filtersRef.current = filters
const [isFilterDialogOpen, setIsFilterDialogOpen] = useState(false)

// NEW orderBy pattern to add immediately after:
const [orderBy, setOrderBy] = useState<OrderByClause[]>(initialState.orderBy)
const orderByRef = useRef(orderBy)
orderByRef.current = orderBy
const [isOrderByPanelOpen, setIsOrderByPanelOpen] = useState(false)
```

**Critical invariant**: `extraStateRef.current` must be updated BEFORE `notify()`. This is the same ordering enforced by `handleSaveFilters` (lines 449-457) and all the CASE/calculated column handlers. Violating this order causes the parent `onChange` to receive stale `orderBy` state.

### 3. `OrderByPanel.tsx` Structure

Model `OrderByPanel.tsx` directly on `FilterConditionsDialog.tsx`. The key differences are:
- Flat `OrderByClause[]` list vs. recursive `FilterGroup` tree — no tree helpers, just array operations
- No "combinator" selector (there is no AND/OR concept for ORDER BY)
- Direction is a `<select>` with two options (ASC / DESC) rather than an adaptive value input
- "Apply" button calls `onSave` then `onClose` — same close-on-apply behavior

The component should NOT be a "live editing" panel (unlike `CaseColumnEditor` / `CalculatedColumnEditor` which emit on every keystroke). Use the Apply/Cancel modal pattern like `FilterConditionsDialog` — edit locally, emit only on Apply.

Do NOT add an `id` field to `OrderByClause`. The type has no `id` and index-based operations are correct. Use array index as the React `key` for clause rows — it is acceptable here since the entire list is replaced on every operation (no cross-position identity).

### 4. `Dialog` Component Import

Use `Dialog`, `DialogContent`, `DialogHeader`, `DialogTitle`, `DialogFooter` from `'@/components/ui/dialog'`. See `FilterConditionsDialog.tsx` lines 1-10 for the exact import block. Do NOT use a custom floating overlay (like CaseColumnEditor's absolute-position div) — the Dialog component matches the Filter pattern and is correct for a form with Apply/Cancel.

### 5. Table/Column Reset on Table Change

When the user changes the table selector for a clause, the column must be reset to the first column of the new table (same pattern as `FilterConditionRow.tsx:handleTableChange` lines 81-87):

```typescript
function handleTableChange(idx: number, tableName: string) {
  const firstCol =
    nodes.find((n) => n.data.tableName === tableName)?.data.columns[0]?.columnName ?? ''
  setLocalOrderBy(updateOrderByClause(localOrderBy, idx, { tableName, columnName: firstCol }))
}
```

### 6. `SELECT_CLASS` Constant

Copy `SELECT_CLASS` directly into `OrderByPanel.tsx` — do NOT import it from `FilterConditionRow.tsx`. The constant is co-located in each file by design:

```typescript
const SELECT_CLASS =
  'h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring'
```

### 7. No New Backend Changes

Story 10.8 is purely frontend:
- No new API endpoints
- No changes to `DatasetSqlGenerator.cs` (that is Story 11.1's responsibility)
- No changes to `BuilderStateDto.cs` (Story 11.1 owns the C# DTO mirror)
- `builder_state.orderBy[]` already flows through `onChange` → parent state → will be serialized by Story 11.2

### 8. Toolbar Comment Update

Line 647 in `QueryBuilderCanvas.tsx` currently reads:
```tsx
{/* Canvas toolbar — Story 10.5: Filter button. Story 10.8 will add Order By. */}
```
Update this comment to:
```tsx
{/* Canvas toolbar — Story 10.5: Filter button. Story 10.8: Order By button. */}
```

### 9. AC-3 Scope: SQL Precedence

Story 10.8 does NOT implement SQL ORDER BY generation. `DatasetSqlGenerator.cs` (Story 11.1) is responsible for iterating `orderBy[]` in declared array order and emitting `ORDER BY "table"."col" ASC|DESC, …`. Story 10.8 only ensures the array is correctly ordered in state so the generator can consume it.

### 10. Deferred Items to Carry Forward in `deferred-work.md`

After implementation, add these to `deferred-work.md`:

- **`orderBy` cascade-delete on canvas node removal deferred to Story 11.1/11.2** — Deleting a canvas node does NOT currently remove ORDER BY clauses that reference that table's columns. Story 11.1 (SQL generation) validates the orderBy array against the current canvas state; Story 11.2 normalizes stale references on restore. [`QueryBuilderCanvas.tsx`]
- **Stale table/column references in ORDER BY clauses on node removal** — Same root cause as the existing filter condition stale-reference defer. When a node is removed, ORDER BY clauses referencing its table are not pruned. [`OrderByPanel.tsx`, `QueryBuilderCanvas.tsx`]
- **`parseBuilderState` normalization for `orderBy` deferred to Story 11.2** — A partial `builder_state` blob missing `orderBy` would yield `undefined`, crashing the canvas (`undefined.length` etc.). Story 11.2 must normalize/default all array fields including `orderBy: []`. Matches the existing deferred item for `caseColumns`/`calculatedColumns`. [`builderState.ts`]
- **No validation gate for ORDER BY clauses** — An empty `tableName`/`columnName` clause is applyable with no client-side feedback. Story 11.1's SQL generator must validate and Story 11.2 must gate Save. No `getOrderByValidationError` SSOT function needed for Story 10.8 (empty `orderBy[]` is valid; Story 11.1 handles degenerate clauses). [`builderState.ts`]

### 11. `@xyflow/react` v12 Typing Rules (Do NOT Apply Here)

`OrderByPanel.tsx` is a standard React component, NOT a React Flow node or edge. The v12 typing rules (NodeProps keyed on Node type; `data` must be `type` alias not `interface`) do NOT apply to this file. `OrderByClause` stays as an `interface` — no change needed.

### Project Structure Notes

- New file: `web/src/components/query-builder/OrderByPanel.tsx` — consistent with other panel components in this directory
- New test file: `web/src/components/query-builder/__tests__/orderByUtils.test.ts` — directory created in Story 10.7 (`filterTreeUtils.test.ts` is already there)
- No new routes, no new API files, no backend changes
- `builderState.ts` is NOT modified — `OrderByClause` interface and `BuilderState.orderBy` already exist

### References

- `builderState.ts`: `web/src/features/datasets/types/builderState.ts`
  - Lines 134-138: `OrderByClause` interface — already complete, do NOT modify
  - Line 172: `BuilderState.orderBy: OrderByClause[]` — already declared
  - Line 183: `EMPTY_BUILDER_STATE.orderBy: []` — already initialized
- `QueryBuilderCanvas.tsx`: `web/src/features/datasets/QueryBuilderCanvas.tsx`
  - Lines 39-56: imports — add `OrderByClause` to the named imports from `'./types/builderState'`
  - Lines 96-101: `extraStateRef` initial value — already contains `orderBy: initialState.orderBy`
  - Lines 123-130: filters state block — add `orderBy` state + ref immediately after
  - Lines 449-457: `handleSaveFilters` — add `handleUpdateOrderBy` immediately after
  - Line 647: toolbar comment — update text
  - Lines 648-657: Filter button — add Order By button inside the same `flex` div
  - Lines 769-775: `FilterConditionsDialog` — add `OrderByPanel` immediately after
- `FilterConditionsDialog.tsx`: `web/src/components/query-builder/FilterConditionsDialog.tsx`
  - Pattern reference for Dialog structure, Apply/Cancel flow, and `useEffect([isOpen])` sync
- `FilterConditionRow.tsx`: `web/src/components/query-builder/FilterConditionRow.tsx`
  - `SELECT_CLASS` constant to copy (not import)
  - Table/column selector and reset-on-table-change pattern (lines 81-87)
- `en.json`: `web/src/lib/i18n/locales/en.json`
  - Add `datasets.builder.orderByPanel` block after `datasets.builder.filterDialog`
- Epics: `_bmad-output/planning-artifacts/epics.md`, Story 10.8 section (FR-69 J-8 AC-1..5)
- Test baseline: 344 tests passed after Story 10.7
- Existing test pattern to follow: `web/src/components/query-builder/__tests__/filterTreeUtils.test.ts`
- Story 10.7 dev notes (previous story): confirms `__tests__/` directory structure + test import patterns

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `npx tsc -b --noEmit` → 0 errors
- `npm run test` → 29 files / 356 tests passed (baseline 344 from Story 10.7 + 12 new helper tests)
- `npm run lint:i18n` → exit 0 (all 16 `orderByPanel` keys resolved as used; none orphaned)

### Completion Notes List

- **Task 1** — Created `OrderByPanel.tsx` modelled on `FilterConditionsDialog.tsx` (Dialog + Apply/Cancel modal pattern; edits a local copy, emits only on Apply). Exported 4 pure module-scope array-mutation helpers (`addOrderByClause`, `removeOrderByClause`, `moveOrderByClause`, `updateOrderByClause`) for unit testing. Flat `OrderByClause[]` operations with index-based addressing (no `id` field, no tree recursion). `SELECT_CLASS` co-located (not imported) per Dev Note §6. Table-change resets the column to the new table's first column (Dev Note §5). `useEffect([isOpen])` local-state sync mirrors the Filter dialog. Cancel button uses the existing `common.cancel` key.
- **Task 2** — Wired `orderBy` state + `orderByRef` + `isOrderByPanelOpen` into `QueryBuilderCanvas.tsx` immediately after the filters block; added `handleUpdateOrderBy` after `handleSaveFilters` (updates `extraStateRef.current.orderBy` BEFORE `notify()` — the critical ordering invariant). Added the "Order By" toolbar button after the "Filter" button and the `<OrderByPanel>` after `<FilterConditionsDialog>`. `extraStateRef` already carried `orderBy: initialState.orderBy`, so no ref-init change was needed. Updated the toolbar comment per Dev Note §8.
- **Task 3** — Added the `datasets.builder.orderByPanel` i18n block to `en.json` after `filterDialog`.
- **Task 4** — Added `__tests__/orderByUtils.test.ts` with 12 tests across the 4 helpers (add ×2, remove ×3, move ×4 incl. both boundary no-ops, update ×3 incl. out-of-range). All green.
- **Task 5** — Verified: tsc 0 errors, 356 tests pass, i18n lint exits 0.
- **Scope boundaries respected**: `builderState.ts` NOT modified (`OrderByClause` + `BuilderState.orderBy` + `EMPTY_BUILDER_STATE.orderBy` already exist). No backend / SQL-generator / DTO changes (Story 11.1/11.2 own those). AC-4 (SQL emission) and AC-6 (persistence) are downstream-story responsibilities; Story 10.8 only guarantees the `orderBy[]` array is correctly ordered and flows through `onChange`.
- Deferred items recorded in `deferred-work.md` per Dev Note §10 (cascade-delete, stale references, `parseBuilderState` normalization, no validation gate, server persistence).

### File List

- `web/src/components/query-builder/OrderByPanel.tsx` (NEW)
- `web/src/components/query-builder/__tests__/orderByUtils.test.ts` (NEW)
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (MODIFIED)
- `web/src/lib/i18n/locales/en.json` (MODIFIED)
- `_bmad-output/implementation-artifacts/deferred-work.md` (MODIFIED)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (MODIFIED)

### Change Log

| Date       | Version | Description                                                                 | Author |
| ---------- | ------- | --------------------------------------------------------------------------- | ------ |
| 2026-06-04 | 1.0     | Implemented Story 10.8 Order By Panel — OrderByPanel component + 4 exported helpers, canvas wiring, i18n keys, 12 unit tests. tsc/tests/i18n all green. | Amelia (dev) |

## Review Findings

- [x] [Review][Patch] Stale closure in `setLocalOrderBy` event handlers — all 7 call sites use `localOrderBy` from the render closure instead of the functional updater form; rapid mutations (double-click Add, batched events) silently overwrite earlier updates. Fix: `setLocalOrderBy(prev => fn(prev, …))` everywhere. [`OrderByPanel.tsx:91, 99, 173, 204, 218, 227, 238`]

- [x] [Review][Defer] `initialState.orderBy` has no undefined/null guard in `useState` initializer — if a persisted state blob predates this field, canvas crashes on `undefined.map`. [`QueryBuilderCanvas.tsx:136`] — deferred, pre-existing (covered by "parseBuilderState normalization for orderBy" in deferred-work.md § implementation of 10-8)
- [x] [Review][Defer] Stale `tableName`/`columnName` in clause rows when a canvas node is removed while the panel is open — table `<select>` enters value-not-in-options state; Apply can persist a clause referencing a deleted table. [`OrderByPanel.tsx:143-163`] — deferred, pre-existing (covered by "Stale table/column references in ORDER BY clauses" in deferred-work.md § implementation of 10-8)
- [x] [Review][Defer] No validation gate prevents saving a clause with blank `tableName`/`columnName` (empty-canvas or zero-column-table edge case). [`OrderByPanel.tsx:119-128`] — deferred, pre-existing (covered by "No validation gate for ORDER BY clauses" in deferred-work.md § implementation of 10-8)
