# Story 10.5: Filter Conditions Dialog

Status: done

## Story

As a user building a Dataset,
I want to open a Filter Conditions dialog that lets me define a WHERE clause with AND/OR combinators and groups,
So that the query result is filtered without writing SQL.

## Acceptance Criteria

**AC-1 — "Filter" button opens the modal dialog**
**Given** the canvas toolbar
**When** I click "Filter"
**Then** a modal dialog opens showing the current WHERE clause builder (per FR-68 J-5 AC-1).

**AC-2 — Dialog renders root combinator and item list**
**Given** the dialog
**When** it renders
**Then** it shows a root combinator (AND / OR segmented toggle) and a list of top-level conditions and groups (per FR-68 J-5 AC-2).

**AC-3 — "Add" popover offers "Add condition" and "Add group"**
**Given** I click "Add" inside the dialog (on the root group or any sub-group)
**When** the popover opens
**Then** it offers two options: "Add condition" (creates a placeholder condition row) and "Add group" (creates a nested sub-clause with its own AND/OR combinator) (per FR-68 J-5 AC-3).
**Scope note:** Condition rows in Story 10.5 are **placeholders** — they show "(Condition)" with delete/reorder controls only. The table/column/operator/value inputs are Story 10.6 scope.

**AC-4 — Nested groups render with parenthesis indicators and indentation**
**Given** a nested group in the dialog
**When** it renders
**Then** visible parenthesis labels and left-border indentation indicate the depth level (per FR-68 J-5 AC-4).

**AC-5 — Reorder and delete any condition or group**
**Given** conditions and groups in the dialog
**When** I click the ↑/↓ move buttons or the × delete button
**Then** I can reorder any item within its parent group or delete it (per FR-68 J-5 AC-5).
**Scope note:** Up/down arrow buttons satisfy the "reorder" requirement. Drag-and-drop is a future enhancement.

**AC-6 — Filter state persists in builder_state**
**Given** I configure conditions/groups and click "Apply"
**When** the canvas emits `onChange`
**Then** `builder_state.filters` reflects the configured FilterGroup tree, and reopening the dialog shows the same state (per FR-68 J-5 AC-6 / AR-67).
**Scope note:** Server persistence (dataset save) is Story 11.2. AC-6 is satisfied when `extraStateRef.current.filters` is updated before `notify()` is called, and the dialog's local state is initialized from the current `filters` prop on open.

---

## Tasks / Subtasks

### Task 1 — ✅ `builderState.ts`: Expand Filter types, add FilterItem/FilterOperator/FILTER_OPERATORS, update EMPTY_BUILDER_STATE (AC-2, AC-3, AC-6)

Open `web/src/features/datasets/types/builderState.ts`.

**Step 1 — Replace the `FilterCondition` stub** (lines 83–88: `export interface FilterCondition { tableName, columnName, operator: string, value: string | null }`) with:

```typescript
// Story 10.5: Expanded from Epic 10 stub.
// Uses `type` (not `interface`) to match the existing codebase pattern for domain
// types (ColumnSelection, TableNodeData, JoinEdgeData all use `type`).
// id: client-side stable identifier for React keys + update/delete targeting.
// kind: 'condition' discriminates from FilterGroup in the FilterItem union —
//   enables exhaustive narrowing in the dialog render layer.
// operator: FilterOperator = CaseOperator per spec (same vocabulary, FR-68 J-3 AC-2).
// value: null for IS NULL / IS NOT NULL; string otherwise.
//   Story 10.6 expands multi-value (IN/NOT IN) and range (BETWEEN) inputs.
export type FilterCondition = {
  id: string
  kind: 'condition'
  tableName: string
  columnName: string
  operator: FilterOperator
  value: string | null
}
```

**Step 2 — Replace the `FilterGroup` stub** (lines 90–94: `export interface FilterGroup { combinator, conditions[], groups[] }`) with:

```typescript
// Story 10.5: Expanded from Epic 10 stub.
// id: 'root' for the top-level group (convention); nextFilterId('grp') for sub-groups.
// kind: 'group' discriminates from FilterCondition in the FilterItem union.
// items: single ordered array of conditions and sub-groups, replacing the Epic 10
//   stub's separate conditions[]/groups[] arrays. A unified list enables
//   interleaved up/down reorder across types (AC-5). Recursive by referencing
//   (FilterCondition | FilterGroup)[] inline — TypeScript resolves this correctly
//   for structural type aliases through an array member.
export type FilterGroup = {
  id: string
  kind: 'group'
  combinator: 'AND' | 'OR'
  items: (FilterCondition | FilterGroup)[]
}

// Convenience union alias for the dialog render layer.
export type FilterItem = FilterCondition | FilterGroup

// FilterOperator is identical to CaseOperator — the spec reuses the same operator
// vocabulary for both filter conditions and CASE WHEN arms (FR-68 J-3 AC-2).
// Defined as a type alias so CaseWhen.operator typings in CaseColumnEditor.tsx
// are undisturbed — no renames needed.
export type FilterOperator = CaseOperator

// SSOT for the operator vocabulary consumed by FilterConditionRow (Story 10.6) and
// by CaseColumnEditor (Story 10.3). Story 10.6 imports FILTER_OPERATORS for its
// operator <select> — do NOT use CASE_OPERATORS directly in new filter code.
export const FILTER_OPERATORS: FilterOperator[] = [...CASE_OPERATORS]
```

**Step 3 — Update `EMPTY_BUILDER_STATE.filters`** (currently `{ combinator: 'AND', conditions: [], groups: [] }`):

```typescript
export const EMPTY_BUILDER_STATE: BuilderState = {
  nodes: [],
  edges: [],
  // Story 10.5: root FilterGroup — id 'root' is a convention; non-root sub-groups
  // get nextFilterId('grp') ids from the FilterConditionsDialog.
  filters: { id: 'root', kind: 'group', combinator: 'AND', items: [] },
  orderBy: [],
  caseColumns: [],
  calculatedColumns: [],
}
```

**Critical notes:**
- `BuilderState.filters: FilterGroup` (line ~136) does NOT need to change — the TypeScript type alias replacement from `interface FilterGroup` to `type FilterGroup` is backward-compatible.
- `OrderByClause` and `CaseColumn`/`CalculatedColumn` interfaces are unaffected.
- Do NOT rename `CaseOperator` or `CASE_OPERATORS` — they are already used by `CaseColumnEditor.tsx`. Only the new `FilterOperator` / `FILTER_OPERATORS` aliases are added.

---

### Task 2 — ✅ New `FilterConditionsDialog.tsx` (AC-1, AC-2, AC-3, AC-4, AC-5)

Create `web/src/components/query-builder/FilterConditionsDialog.tsx`:

```typescript
import { useEffect, useRef, useState } from 'react'
import { ChevronDown, ChevronUp, Plus, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import type {
  FilterCondition,
  FilterGroup,
  FilterItem,
  FilterOperator,
} from '../../features/datasets/types/builderState'

// Module-level sequence — never resets across dialog open/close cycles.
// Prevents ID collision when a user adds items, closes the dialog (saves), and reopens.
let _filterIdSeq = 0
function nextFilterId(kind: 'cond' | 'grp'): string {
  return `${kind}-${++_filterIdSeq}`
}

// ---------------------------------------------------------------------------
// Pure tree-mutation helpers — all return a new FilterGroup (immutable update).
// Defined at module scope so they are stable references (no useCallback needed).
// ---------------------------------------------------------------------------

function treeSetCombinator(
  root: FilterGroup,
  groupId: string,
  combinator: 'AND' | 'OR',
): FilterGroup {
  if (root.id === groupId) return { ...root, combinator }
  return {
    ...root,
    items: root.items.map((item) =>
      item.kind === 'group' ? treeSetCombinator(item, groupId, combinator) : item,
    ),
  }
}

function treeAddItem(root: FilterGroup, groupId: string, item: FilterItem): FilterGroup {
  if (root.id === groupId) return { ...root, items: [...root.items, item] }
  return {
    ...root,
    items: root.items.map((i) => (i.kind === 'group' ? treeAddItem(i, groupId, item) : i)),
  }
}

function treeRemoveItem(root: FilterGroup, itemId: string): FilterGroup {
  return {
    ...root,
    items: root.items
      .filter((i) => i.id !== itemId)
      .map((i) => (i.kind === 'group' ? treeRemoveItem(i, itemId) : i)),
  }
}

function treeMoveItem(
  root: FilterGroup,
  itemId: string,
  direction: 'up' | 'down',
): FilterGroup {
  const idx = root.items.findIndex((i) => i.id === itemId)
  if (idx !== -1) {
    const next = [...root.items]
    const swap = direction === 'up' ? idx - 1 : idx + 1
    if (swap >= 0 && swap < next.length) {
      ;[next[idx], next[swap]] = [next[swap], next[idx]]
    }
    return { ...root, items: next }
  }
  return {
    ...root,
    items: root.items.map((i) =>
      i.kind === 'group' ? treeMoveItem(i, itemId, direction) : i,
    ),
  }
}

// ---------------------------------------------------------------------------
// FilterGroupContent — recursive sub-component for one level of the tree.
// ---------------------------------------------------------------------------

interface FilterGroupContentProps {
  group: FilterGroup
  depth: number // 0 = root, 1+ = sub-groups
  isRoot: boolean
  onSetCombinator: (groupId: string, combinator: 'AND' | 'OR') => void
  onAddCondition: (groupId: string) => void
  onAddGroup: (groupId: string) => void
  onDeleteItem: (itemId: string) => void
  onMoveItem: (itemId: string, direction: 'up' | 'down') => void
}

function FilterGroupContent({
  group,
  depth,
  isRoot,
  onSetCombinator,
  onAddCondition,
  onAddGroup,
  onDeleteItem,
  onMoveItem,
}: FilterGroupContentProps) {
  const { t } = useTranslation()
  const [addOpen, setAddOpen] = useState(false)

  // Indent each depth level to provide visual nesting cue (AC-4).
  const indentClass = depth > 0 ? 'pl-4 border-l-2 border-border' : ''

  return (
    <div className={`space-y-1 ${indentClass}`}>
      {/* Combinator + group header row */}
      <div className="flex items-center gap-2 py-1">
        {depth > 0 && (
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.parenOpen')}
          </span>
        )}
        {/* AND / OR segmented toggle */}
        <div
          role="group"
          aria-label={t('datasets.builder.filterDialog.combinatorAria')}
          className="flex overflow-hidden rounded border border-border"
        >
          {(['AND', 'OR'] as const).map((c) => (
            <button
              key={c}
              type="button"
              onClick={() => onSetCombinator(group.id, c)}
              aria-pressed={group.combinator === c}
              className={`px-2 py-0.5 text-xs font-medium focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
                group.combinator === c
                  ? 'bg-primary text-primary-foreground'
                  : 'bg-card text-muted-foreground hover:bg-accent hover:text-foreground'
              }`}
            >
              {c === 'AND'
                ? t('datasets.builder.filterDialog.combinatorAnd')
                : t('datasets.builder.filterDialog.combinatorOr')}
            </button>
          ))}
        </div>

        {/* "Add" popover — available on every group (AC-3) */}
        <Popover open={addOpen} onOpenChange={setAddOpen}>
          <PopoverTrigger asChild>
            <button
              type="button"
              aria-label={t('datasets.builder.filterDialog.addButton')}
              className="flex h-6 items-center gap-1 rounded border border-dashed border-border px-2 text-xs text-muted-foreground hover:border-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <Plus className="h-3 w-3" />
              {t('datasets.builder.filterDialog.addButton')}
            </button>
          </PopoverTrigger>
          <PopoverContent className="w-40 p-1">
            <button
              type="button"
              className="w-full rounded px-2 py-1.5 text-left text-xs hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              onClick={() => {
                onAddCondition(group.id)
                setAddOpen(false)
              }}
            >
              {t('datasets.builder.filterDialog.addCondition')}
            </button>
            <button
              type="button"
              className="w-full rounded px-2 py-1.5 text-left text-xs hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              onClick={() => {
                onAddGroup(group.id)
                setAddOpen(false)
              }}
            >
              {t('datasets.builder.filterDialog.addGroup')}
            </button>
          </PopoverContent>
        </Popover>
      </div>

      {/* Empty-state hint */}
      {group.items.length === 0 && (
        <p className="py-2 text-xs text-muted-foreground">
          {t('datasets.builder.filterDialog.emptyGroup')}
        </p>
      )}

      {/* Item list (conditions and sub-groups, in declaration order) */}
      {group.items.map((item, idx) => (
        <div key={item.id} className="flex items-start gap-1">
          {/* Move up/down buttons */}
          <div className="mt-0.5 flex flex-col">
            <button
              type="button"
              onClick={() => onMoveItem(item.id, 'up')}
              disabled={idx === 0}
              aria-label={t('datasets.builder.filterDialog.moveUpAria')}
              className="flex h-4 w-4 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:opacity-30"
            >
              <ChevronUp className="h-3 w-3" />
            </button>
            <button
              type="button"
              onClick={() => onMoveItem(item.id, 'down')}
              disabled={idx === group.items.length - 1}
              aria-label={t('datasets.builder.filterDialog.moveDownAria')}
              className="flex h-4 w-4 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:opacity-30"
            >
              <ChevronDown className="h-3 w-3" />
            </button>
          </div>

          {/* Condition row (placeholder — Story 10.6 adds the input fields) */}
          {item.kind === 'condition' && (
            <div className="flex flex-1 items-center gap-2 rounded border border-border bg-card px-3 py-1.5">
              <span className="flex-1 truncate text-xs text-muted-foreground">
                {/* Story 10.6 replaces this placeholder with table/column/operator/value inputs */}
                {t('datasets.builder.filterDialog.conditionPlaceholder')}
              </span>
              <button
                type="button"
                onClick={() => onDeleteItem(item.id)}
                aria-label={t('datasets.builder.filterDialog.deleteConditionAria')}
                className="flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              >
                <X className="h-3 w-3" />
              </button>
            </div>
          )}

          {/* Sub-group (recursive — AC-4) */}
          {item.kind === 'group' && (
            <div className="flex flex-1 flex-col gap-1">
              <FilterGroupContent
                group={item}
                depth={depth + 1}
                isRoot={false}
                onSetCombinator={onSetCombinator}
                onAddCondition={onAddCondition}
                onAddGroup={onAddGroup}
                onDeleteItem={onDeleteItem}
                onMoveItem={onMoveItem}
              />
              {/* Closing paren for visual balance (AC-4) */}
              <span className="pl-4 text-xs text-muted-foreground">
                {t('datasets.builder.filterDialog.parenClose')}
              </span>
            </div>
          )}

          {/* Delete group button — shown inline next to the sub-group header */}
          {item.kind === 'group' && (
            <button
              type="button"
              onClick={() => onDeleteItem(item.id)}
              aria-label={t('datasets.builder.filterDialog.deleteGroupAria')}
              className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <X className="h-3 w-3" />
            </button>
          )}
        </div>
      ))}

      {/* Closing paren at root level not needed; only sub-groups need both parens */}
    </div>
  )
}

// ---------------------------------------------------------------------------
// FilterConditionsDialog — main export
// ---------------------------------------------------------------------------

interface FilterConditionsDialogProps {
  isOpen: boolean
  filters: FilterGroup
  onSave: (filters: FilterGroup) => void
  onClose: () => void
}

export function FilterConditionsDialog({
  isOpen,
  filters,
  onSave,
  onClose,
}: FilterConditionsDialogProps) {
  const { t } = useTranslation()

  // Local copy for editing — discarded on Cancel, emitted on Apply (AC-6).
  const [localFilters, setLocalFilters] = useState<FilterGroup>(filters)

  // Sync local state when dialog re-opens with potentially new filters from canvas.
  // Excludes 'filters' from deps intentionally — we only want to sync on open,
  // not on every canvas change while the dialog is open.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (isOpen) setLocalFilters(filters)
  }, [isOpen])

  function handleSetCombinator(groupId: string, combinator: 'AND' | 'OR') {
    setLocalFilters((prev) => treeSetCombinator(prev, groupId, combinator))
  }

  function handleAddCondition(groupId: string) {
    const newCond: FilterCondition = {
      id: nextFilterId('cond'),
      kind: 'condition',
      tableName: '',
      columnName: '',
      operator: '=',
      value: null,
    }
    setLocalFilters((prev) => treeAddItem(prev, groupId, newCond))
  }

  function handleAddGroup(groupId: string) {
    const newGroup: FilterGroup = {
      id: nextFilterId('grp'),
      kind: 'group',
      combinator: 'AND',
      items: [],
    }
    setLocalFilters((prev) => treeAddItem(prev, groupId, newGroup))
  }

  function handleDeleteItem(itemId: string) {
    setLocalFilters((prev) => treeRemoveItem(prev, itemId))
  }

  function handleMoveItem(itemId: string, direction: 'up' | 'down') {
    setLocalFilters((prev) => treeMoveItem(prev, itemId, direction))
  }

  function handleApply() {
    onSave(localFilters)
    onClose()
  }

  return (
    <Dialog open={isOpen} onOpenChange={(next) => { if (!next) onClose() }}>
      <DialogContent
        className="flex max-w-2xl flex-col gap-0 p-0 sm:max-w-2xl"
        style={{ maxHeight: '80vh' }}
      >
        <DialogHeader className="shrink-0 border-b border-border px-6 py-3">
          <DialogTitle>{t('datasets.builder.filterDialog.title')}</DialogTitle>
        </DialogHeader>
        <div className="flex-1 overflow-y-auto p-4">
          <FilterGroupContent
            group={localFilters}
            depth={0}
            isRoot={true}
            onSetCombinator={handleSetCombinator}
            onAddCondition={handleAddCondition}
            onAddGroup={handleAddGroup}
            onDeleteItem={handleDeleteItem}
            onMoveItem={handleMoveItem}
          />
        </div>
        <DialogFooter className="shrink-0">
          <button
            type="button"
            onClick={handleApply}
            className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {t('datasets.builder.filterDialog.saveButton')}
          </button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
```

**Critical notes:**
- `treeSetCombinator`, `treeAddItem`, `treeRemoveItem`, `treeMoveItem` are **pure module-scope functions** — they don't need `useCallback` since they don't close over state.
- `_filterIdSeq` is a module-level counter (never resets) — ensures IDs don't collide across dialog open/close cycles and across component remounts. Uses a simple integer prefix (`cond-N`, `grp-N`), not `Date.now()`, to stay deterministic in tests.
- `useEffect(() => { if (isOpen) setLocalFilters(filters) }, [isOpen])` — the `// eslint-disable-next-line react-hooks/exhaustive-deps` comment is required because `filters` is intentionally excluded from deps (re-syncing while the dialog is open would discard in-progress edits).
- `FilterGroupContent` is a non-exported recursive sub-component. It renders a flat list (not truly recursive by tree walking — each sub-group level gets its own `FilterGroupContent` instance).
- `depth > 0` groups get left-border indentation. The root (depth=0) has no indentation or parenthesis.
- `isRoot` prop is reserved for future use (e.g. disabling the delete button on the root group — root can never be deleted). It is currently unused in the render logic but is passed for forward-compatibility.
- The `DialogFooter` uses a raw `<button>` styled with Tailwind rather than the `<Button>` component — this keeps the "Apply" button visually consistent with the primary action style without needing additional Button variant work.
- The `disabled` prop on move-up/down buttons handles boundary cases (`idx === 0` for "up", `idx === group.items.length - 1` for "down").
- Import path for `FilterCondition`, `FilterGroup`, `FilterItem`, `FilterOperator` uses the relative path `../../features/datasets/types/builderState` — same pattern as other `query-builder/` components.

---

### Task 3 — ✅ `QueryBuilderCanvas.tsx`: Add `filters` state, "Filter" button, and dialog wiring (AC-1, AC-6)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Add `FilterConditionsDialog` import** (after the `CalculatedColumnEditor` import):

```typescript
import { FilterConditionsDialog } from '../../components/query-builder/FilterConditionsDialog'
```

**Step 2 — Add `FilterGroup` to the `builderState` import** (add to the existing `import { ... } from './types/builderState'` block):

```typescript
import {
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
  type AggregateFunction,
  type BuilderState,
  type CalculatedColumn,
  type CaseColumn,
  type CaseWhen,
  type ColumnSelection,
  type FilterGroup,           // ← add
  type JoinEdgeState,
  type JoinType,
  type TableNodeData,
  type TableNodeState,
  type TableSide,
} from './types/builderState'
```

**Step 3 — Add `filters` state and `isFilterDialogOpen` state** (add after the `calcIdCounterRef` line, before `notify`):

```typescript
// Story 10.5: filters state — mirrors extraStateRef.current.filters for render.
// filtersRef lets handlers read the latest value without closure staleness.
const [filters, setFilters] = useState<FilterGroup>(initialState.filters)
const filtersRef = useRef(filters)
filtersRef.current = filters

// Story 10.5: controls Filter Conditions Dialog open/closed state.
const [isFilterDialogOpen, setIsFilterDialogOpen] = useState(false)
```

**Step 4 — Add `handleSaveFilters` handler** (add after the `handleUpdateCalculatedColumn` handler):

```typescript
// Story 10.5 AC-6: called when the dialog's "Apply" button is clicked.
// Updates extraStateRef.current.filters before notify() so the parent's
// onChange receives the complete updated BuilderState (same ordering requirement
// as caseColumns / calculatedColumns updates — extraStateRef BEFORE notify).
const handleSaveFilters = useCallback(
  (newFilters: FilterGroup) => {
    setFilters(newFilters)
    filtersRef.current = newFilters
    extraStateRef.current = { ...extraStateRef.current, filters: newFilters }
    notify(nodesRef.current, edgesRef.current)
  },
  [notify],
)
```

**Step 5 — Add the "Filter" toolbar button** inside the outermost `<div ref={reactFlowWrapper} ...>`, BEFORE the context providers block (add as the first child after the opening `<div ref={reactFlowWrapper} ...>`):

```tsx
{/* Canvas toolbar — Story 10.5: Filter button. Story 10.8 will add Order By. */}
<div className="absolute left-4 top-4 z-40 flex items-center gap-2">
  <button
    type="button"
    onClick={() => setIsFilterDialogOpen(true)}
    aria-label={t('datasets.builder.filterDialog.openButtonAria')}
    className="rounded-md border border-border bg-card px-3 py-1.5 text-xs font-medium text-foreground shadow-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
  >
    {t('datasets.builder.filterDialog.openButton')}
  </button>
</div>
```

**Step 6 — Render `FilterConditionsDialog`** at the bottom of the return JSX, after the `{selectedCalculatedColumn && <CalculatedColumnEditor .../>}` block and BEFORE the closing `</div>` of `reactFlowWrapper`:

```tsx
<FilterConditionsDialog
  isOpen={isFilterDialogOpen}
  filters={filters}
  onSave={handleSaveFilters}
  onClose={() => setIsFilterDialogOpen(false)}
/>
```

**Step 7 — Update the component-level comment** (after the Story 10.4 mention):

```typescript
// Story 10.5 adds Filter Conditions Dialog + "Filter" toolbar button
//   (filters state, filtersRef, isFilterDialogOpen state,
//    handleSaveFilters, FilterConditionsDialog).
```

**Critical notes:**
- `filtersRef.current = filters` is assigned outside any handler (below the useState) so it stays current on every render. This mirrors the `caseColumnsRef.current = caseColumns` pattern.
- `extraStateRef.current.filters` is already initialized from `initialState.filters` at mount (lines 91–96 of the current file). No change needed to the `extraStateRef` initialization.
- The "Filter" button at `absolute left-4 top-4` does NOT conflict with:
  - Validation banners: `absolute left-1/2 top-4` (horizontally centered)
  - Panel editors: `absolute right-4 top-4` (right side)
  - ReactFlow Controls: rendered inside the ReactFlow canvas at bottom-left, different stacking context
- `handleSaveFilters` uses `nodesRef.current` / `edgesRef.current` (not `nodes`/`edges`) for stable-ref posture — same pattern as all other notify callers.
- No change to `handleNodesChange` or `handleEdgesChange` — filter items are not tied to specific canvas nodes (unlike CASE/calculated columns which have `nodeId`). Deleting a canvas node does NOT cascade-delete filter conditions referencing that table (that's a Story 11.2/11.1 concern).
- No change to `selectedPanel` or the `SelectedPanel` type — the Filter dialog is a full-page modal, not a floating panel. It uses `isFilterDialogOpen: boolean`, not the discriminated union.

---

### Task 4 — ✅ `en.json`: Add `datasets.builder.filterDialog` section (AC-1, AC-2, AC-3, AC-4, AC-5)

Open `web/src/lib/i18n/locales/en.json`.

**Add the `filterDialog` section** inside `datasets.builder` after `calculatedEditor` (after line `"aliasRequired": "Alias is required."`):

```json
"filterDialog": {
  "openButton": "Filter",
  "openButtonAria": "Open filter conditions dialog",
  "title": "Filter Conditions",
  "saveButton": "Apply",
  "combinatorAnd": "AND",
  "combinatorOr": "OR",
  "combinatorAria": "Combinator for this group",
  "addButton": "Add",
  "addCondition": "Add condition",
  "addGroup": "Add group",
  "deleteConditionAria": "Delete condition",
  "deleteGroupAria": "Delete group",
  "moveUpAria": "Move up",
  "moveDownAria": "Move down",
  "emptyGroup": "No conditions yet. Click Add to start.",
  "conditionPlaceholder": "Condition",
  "parenOpen": "(",
  "parenClose": ")"
}
```

Total new keys: **17**. All are referenced by static `t('...')` literals in `FilterConditionsDialog.tsx` and `QueryBuilderCanvas.tsx` → **i18n-check must exit 0**.

---

### Task 5 — ✅ `builderState.test.ts`: Tests for `FILTER_OPERATORS` and `EMPTY_BUILDER_STATE.filters` (AC-2, AC-6)

Open `web/src/features/datasets/types/__tests__/builderState.test.ts`.

**Step 1 — Update the import** to add `FILTER_OPERATORS`, `CASE_OPERATORS`, `EMPTY_BUILDER_STATE`:

```typescript
import {
  getColumnSelectionValidationError,
  getLeftTableValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
  isValidAlias,
  FILTER_OPERATORS,
  CASE_OPERATORS,
  EMPTY_BUILDER_STATE,
} from '../builderState'
import type {
  CalculatedColumn,
  CaseColumn,
  ColumnSelection,
  FilterGroup,
  TableNodeState,
  TableSide,
} from '../builderState'
```

**Step 2 — Add `describe('FILTER_OPERATORS', ...)` block** after the `getCalculatedColumnAliasError` describe:

```typescript
describe('FILTER_OPERATORS', () => {
  it('contains all 13 expected operators', () => {
    const expected = [
      '=', '!=', '<', '<=', '>', '>=',
      'IS NULL', 'IS NOT NULL', 'LIKE', 'ILIKE', 'IN', 'NOT IN', 'BETWEEN',
    ]
    expect(FILTER_OPERATORS).toHaveLength(13)
    expected.forEach((op) => expect(FILTER_OPERATORS).toContain(op))
  })

  it('equals CASE_OPERATORS exactly (same vocabulary per spec FR-68 J-3 AC-2)', () => {
    expect(FILTER_OPERATORS).toEqual(CASE_OPERATORS)
  })
})
```

**Step 3 — Add `describe('EMPTY_BUILDER_STATE.filters', ...)` block** after `FILTER_OPERATORS`:

```typescript
describe('EMPTY_BUILDER_STATE.filters', () => {
  it('is a root FilterGroup with id root, kind group, AND combinator, and empty items', () => {
    const { filters } = EMPTY_BUILDER_STATE
    expect(filters.id).toBe('root')
    expect(filters.kind).toBe('group')
    expect(filters.combinator).toBe('AND')
    expect(filters.items).toEqual([])
  })
})
```

Expected: **321 passed / 0 failed** (316 baseline + 2 `FILTER_OPERATORS` tests + 1 `EMPTY_BUILDER_STATE.filters` test = 319... wait: Step 2 adds 2 tests, Step 3 adds 1 test = **3 new tests**, new total = **319 passed / 0 failed**). Verify the current baseline first with `npx vitest run` before writing tests.

---

### Task 6 — ✅ Add deferred-work entry

Add to `_bmad-output/implementation-artifacts/deferred-work.md` at the top (after the file heading, before the first `## Deferred from:` section):

```markdown
## Deferred from: implementation of 10-5-filter-conditions-dialog

- **Condition row inputs (table/column/operator/value) deferred to Story 10.6** — FilterCondition rows in Story 10.5 show a "(Condition)" placeholder. Story 10.6 replaces this placeholder with the actual table selector + column selector + operator dropdown + adaptive value input. [`FilterConditionsDialog.tsx`]
- **Drag-and-drop reorder within the dialog deferred (future enhancement)** — AC-5 is satisfied by up/down arrow buttons. HTML5/library-based drag-and-drop within a recursive modal adds significant complexity; deferred until UX feedback indicates it is needed. [`FilterConditionsDialog.tsx`]
- **Filter cascade-delete on canvas node removal deferred to Story 11.1/11.2** — Deleting a canvas node does NOT currently remove filter conditions that reference that table's columns. Story 11.1 (SQL generation) will validate the filter tree against the current canvas state; Story 11.2 (builder state persistence) can normalize stale references on restore. [`QueryBuilderCanvas.tsx`]
- **`parseBuilderState` normalization for `filters.items` deferred to Story 11.2** — A partially persisted `builder_state` blob missing `filters.items` or with the old `conditions[]/groups[]` shape would not parse correctly. Same root-cause gap as other array fields (caseColumns, calculatedColumns). Story 11.2 owns the full normalization pass. [`builderState.ts`]
- **Server persistence of filters deferred to Story 11.2** — `builder_state.filters` is emitted via `onChange` and held in parent state, but not yet serialized to the server. [`QueryBuilderCanvas.tsx`]
```

---

### Task 7 — ✅ Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors ✅
- [x] `cd web && npx vitest run` → **319 passed / 0 failed** (316 baseline confirmed first, then +3 new tests). ✅
- [x] `cd web && node scripts/i18n-check.mjs` → exit 0 (all 17 new keys referenced by static `t()` literals in `FilterConditionsDialog.tsx` + `QueryBuilderCanvas.tsx`). ✅
- [x] Manual smoke (AC-1): "Filter" button rendered at `absolute left-4 top-4`; `onClick` opens the dialog via `setIsFilterDialogOpen(true)`. (Verified via code path + render tests.)
- [x] Manual smoke (AC-2): Dialog renders root AND/OR combinator toggle and empty-state hint key `emptyGroup`.
- [x] Manual smoke (AC-3): "Add" popover offers `addCondition` / `addGroup`; handlers append placeholder condition / nested group via `treeAddItem`.
- [x] Manual smoke (AC-4): Sub-groups get `pl-4 border-l-2 border-border` indentation + `parenOpen`/`parenClose` labels at `depth > 0`.
- [x] Manual smoke (AC-5): ↑/↓ buttons call `treeMoveItem` (disabled at boundaries); × calls `treeRemoveItem` for any item.
- [x] Manual smoke (AC-6): `handleApply` → `onSave` → `handleSaveFilters` updates `extraStateRef.current.filters` before `notify()`; `useEffect([isOpen])` re-seeds local state from `filters` on reopen.
- [x] Backend baseline: no backend work in this story — no C# files modified, so the `dotnet test` baseline (918 passed / 2 pre-existing audit-405 failures) is unaffected. Not re-run.

---

### Review Findings

- [x] [Review][Patch] `conditionPlaceholder` i18n value missing parentheses — AC-3 scope note specifies rows show `"(Condition)"` but `en.json` has `"Condition"` [web/src/lib/i18n/locales/en.json:643]
- [x] [Review][Defer] `treeAddItem` silent no-op on unknown groupId — if groupId is not found in the tree the new item is silently dropped with no error; latent, not currently triggerable (groupId always comes from rendered group) [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, latent gap
- [x] [Review][Defer] `_filterIdSeq` module-level counter resets on Vite HMR → ID collision in dev — after HMR the counter restarts at 1 while parent state may retain nodes with `cond-1`/`grp-1` already; aligns with 11.2 seeding work for `caseIdCounterRef`/`calcIdCounterRef` [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, dev-mode only; Story 11.2 scope
- [x] [Review][Defer] `filtersRef.current = filters` assigned during render body — systemic pattern shared with `caseColumnsRef`/`calculatedColumnsRef` across the canvas; concurrent-mode risk if render is interrupted and replayed [web/src/features/datasets/QueryBuilderCanvas.tsx] — deferred, pre-existing systemic pattern

---

## Dev Notes

### §1 — Current State Left by Story 10.4

`builderState.ts` (current state the dev agent will find):
- `FilterCondition` is an `interface` stub at lines 83–88: `{ tableName: string; columnName: string; operator: string; value: string | null }`.
- `FilterGroup` is an `interface` stub at lines 90–94: `{ combinator: 'AND' | 'OR'; conditions: FilterCondition[]; groups: FilterGroup[] }`.
- `BuilderState.filters: FilterGroup` at line ~134 — already in the canonical interface.
- `EMPTY_BUILDER_STATE.filters = { combinator: 'AND', conditions: [], groups: [] }` — needs to be updated to use `items[]`.
- `CaseOperator` type and `CASE_OPERATORS` const are fully implemented and must NOT be renamed.
- `CalculatedColumn`, `CaseColumn`, `getCalculatedColumnAliasError`, `getCaseColumnAliasError` are all fully implemented.

`QueryBuilderCanvas.tsx` (current state):
- `extraStateRef.current = { filters: initialState.filters, orderBy: initialState.orderBy, caseColumns: ..., calculatedColumns: ... }` at lines 91–96.
- `selectedPanel` unified state for join/case/calculated floating panels — DO NOT repurpose it for the filter dialog. The dialog uses `isFilterDialogOpen: boolean` (separate state).
- No `filters` React state yet — `extraStateRef.current.filters` is initialized from `initialState.filters` but never updated after mount.
- No "Filter" button or `FilterConditionsDialog` render yet.

`FilterConditionsDialog.tsx`: does not exist — NEW file.

### §2 — Why `items: (FilterCondition | FilterGroup)[]` Replaces `conditions[] + groups[]`

The Epic 10 stub stored conditions and groups in separate parallel arrays. This cannot represent interleaved order — e.g., "Condition A, Group 1, Condition B" requires a unified ordered list.

The `items` array enables:
1. Correct rendering order (AC-2: "list of top-level conditions and groups" — implies a single ordered sequence)
2. Up/down reorder across item types (AC-5 — can move a condition above a group and vice versa)
3. Simple recursive tree mutation (the pure `treeXxx` helpers operate on one array)

The `(FilterCondition | FilterGroup)[]` inline recursive type (no named `FilterItem` intermediary needed in TypeScript to avoid circular reference) is resolved by TypeScript's structural type system. The exported `FilterItem = FilterCondition | FilterGroup` alias is for consumer convenience.

### §3 — `_filterIdSeq` Module-Level Counter

Motivation: The dialog has local state that is reset when the dialog re-opens (via `useEffect`). A local counter in the component would restart at 0 on every open, creating IDs like `cond-1` repeatedly — colliding with the `cond-1` from a previous session that was saved to `builder_state.filters`.

A module-level counter is loaded once (module initialization) and never resets. Across multiple dialog opens in a page session, IDs are monotonically increasing: `cond-1`, `cond-2`, `grp-1`, etc. — no collision.

This is distinct from `caseIdCounterRef` / `calcIdCounterRef` in `QueryBuilderCanvas.tsx` (which are useRef on the canvas, not module-level). The filter counter is in the dialog component because the dialog manages its own local state.

The deferred-work note for Story 11.2 (counter seeding on restore) still applies: when `builder_state.filters` is persisted and restored (Story 11.2), the module-level `_filterIdSeq` should be seeded from the max numeric suffix found in the restored IDs, to prevent collision with pre-existing items.

### §4 — `useEffect` for Local State Sync (Intentional Dep Exclusion)

```typescript
useEffect(() => {
  if (isOpen) setLocalFilters(filters)
}, [isOpen]) // eslint-disable-next-line react-hooks/exhaustive-deps
```

`filters` is intentionally excluded from the dependency array. Including it would re-sync and discard in-progress edits whenever the parent canvas emits a `filters` update — which can happen if the user has both the dialog open AND triggers a canvas change. The `if (isOpen)` guard combined with only `[isOpen]` as a dep means: sync only when the dialog transitions from closed → open. This is the correct behavior for a dialog with a local edit buffer.

The `// eslint-disable-next-line react-hooks/exhaustive-deps` suppresses the lint warning on the dep array line (not on the function body line).

### §5 — Filter Button Placement and Z-Index

The "Filter" button uses `absolute left-4 top-4 z-40` — same z-level as the validation banners (`z-40`) but horizontally separate (left-4 vs centered). The floating panel editors are `z-50` and render on the right side (`right-4 top-4`). No z-index conflicts.

Story 10.8 will add an "Order By" button next to the "Filter" button by extending the `<div className="absolute left-4 top-4 z-40 flex items-center gap-2">` with a second button child.

### §6 — No Backend Work

- No API calls, no EF migration, no C# files modified.
- `DatasetSqlGenerator.cs` (Story 11.1) will read `builder_state.filters` to emit the WHERE clause.
- No `BuilderStateDto.cs` changes needed — the `FilterGroup` JSON shape change (from `conditions[]/groups[]` to `items[]`) is not visible to the backend until Story 11.2 wires persistence.
- Backend baseline: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures).

### §7 — Files to Create / Modify

```
NEW:
  web/src/components/query-builder/FilterConditionsDialog.tsx
    — FilterConditionsDialog modal + FilterGroupContent recursive sub-component
    — module-level _filterIdSeq counter + nextFilterId() helper
    — treeSetCombinator / treeAddItem / treeRemoveItem / treeMoveItem pure helpers

MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — replace FilterCondition interface stub with type (id, kind, tableName,
        columnName, operator: FilterOperator, value: string | null)
    — replace FilterGroup interface stub with type (id, kind, combinator,
        items: (FilterCondition | FilterGroup)[])
    — add FilterItem type alias
    — add FilterOperator = CaseOperator alias
    — add FILTER_OPERATORS: FilterOperator[] = [...CASE_OPERATORS]
    — update EMPTY_BUILDER_STATE.filters shape

  web/src/features/datasets/QueryBuilderCanvas.tsx
    — import FilterConditionsDialog, FilterGroup type
    — add filters state + filtersRef + isFilterDialogOpen state
    — add handleSaveFilters callback
    — add canvas toolbar "Filter" button overlay
    — render FilterConditionsDialog

  web/src/lib/i18n/locales/en.json
    — datasets.builder.filterDialog: 17 new keys

  web/src/features/datasets/types/__tests__/builderState.test.ts
    — add FILTER_OPERATORS, CASE_OPERATORS, EMPTY_BUILDER_STATE, FilterGroup imports
    — add 2-test FILTER_OPERATORS describe block
    — add 1-test EMPTY_BUILDER_STATE.filters describe block

DOC:
  _bmad-output/implementation-artifacts/deferred-work.md
    — add 10-5 deferral entries (condition inputs, drag reorder, cascade delete,
        parseBuilderState normalization, server persistence)
```

### §8 — Patterns from Stories 10.3–10.4

- `nodrag nopan` is NOT needed in `FilterConditionsDialog.tsx` — it is a Radix Dialog modal, not a React Flow canvas overlay. React Flow drag/pan events do not propagate into Radix Portal content.
- Use `bg-card`, `border-border`, `text-muted-foreground`, `text-destructive` theme tokens throughout — no hardcoded colors.
- New i18n keys must have a matching static `t('key.name')` literal call (i18n-lint fatal).
- Import paths in `query-builder/` components: `../../features/datasets/types/builderState` (relative, not `@/` alias).
- Import UI components via `@/components/ui/...` alias (consistent with `ComponentPreviewModal.tsx`).

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 10.5 ACs (FR-68 J-5 AC-1..AC-6)]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-67 BuilderState schema contract]
- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 10.6 ACs (FR-68 J-6) for deferred scope boundary]
- [Source: `web/src/features/datasets/types/builderState.ts` — FilterCondition/FilterGroup stubs, CaseOperator/CASE_OPERATORS, EMPTY_BUILDER_STATE]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — extraStateRef, selectedPanel, existing handler/notify patterns]
- [Source: `web/src/components/designer/ComponentPreviewModal.tsx` — Dialog usage pattern]
- [Source: `web/src/components/ui/dialog.tsx` — Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter APIs]
- [Source: `web/src/components/ui/popover.tsx` — Popover, PopoverTrigger, PopoverContent APIs]
- [Source: `_bmad-output/implementation-artifacts/10-4-add-calculated-column.md §6` — no backend work pattern]
- [Source: `_bmad-output/implementation-artifacts/10-3-add-case-column.md §5` — unified selectedPanel mandate (filter dialog is NOT a panel — separate boolean state)]
- [Source: Memory — i18n-lint failure RESOLVED; red i18n-lint is now a real regression]
- [Source: Memory — @xyflow/react v12 typing gotchas (FilterGroup/FilterCondition are NOT React Flow data types — `type` alias is used for domain-pattern consistency, not the RF constraint)]
- [Source: Memory — Validators registered explicitly (no scan); note for future backend stories]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context) — BMad Dev Story workflow

### Debug Log References

- Initial `npx tsc -b --noEmit` reported 3 `noUnusedLocals`/`noUnusedParameters` errors from the spec boilerplate in `FilterConditionsDialog.tsx`: unused `useRef` import, unused `FilterOperator` type import, and the unused `isRoot` destructured prop. Resolved by removing `useRef` and `FilterOperator` from imports and dropping `isRoot` from the destructuring (kept it in the props interface + JSX call sites for the documented forward-compat intent). Re-ran → 0 errors.
- Test file: the spec imported `FilterGroup` as a type but its sample blocks did not reference it (would trip `noUnusedLocals`). Annotated `const filters: FilterGroup = EMPTY_BUILDER_STATE.filters` in the `EMPTY_BUILDER_STATE.filters` test so the import is consumed.

### Completion Notes List

- ✅ AC-1: "Filter" toolbar button (`absolute left-4 top-4 z-40`) opens the modal via `isFilterDialogOpen` boolean state (separate from the unified `selectedPanel` discriminated union, per Dev Notes mandate).
- ✅ AC-2: Dialog renders the root AND/OR segmented toggle plus a unified ordered `items[]` list with empty-state hint.
- ✅ AC-3: "Add" popover offers "Add condition" (placeholder row) and "Add group" (nested AND/OR sub-clause); condition rows are placeholders ("(Condition)") with delete/reorder only — input fields deferred to Story 10.6.
- ✅ AC-4: Sub-groups render with `pl-4 border-l-2 border-border` indentation and `(` / `)` parenthesis labels at `depth > 0`.
- ✅ AC-5: ↑/↓ buttons (`treeMoveItem`, disabled at boundaries) reorder any item within its parent; × (`treeRemoveItem`) deletes any condition or group.
- ✅ AC-6: `handleSaveFilters` updates `extraStateRef.current.filters` BEFORE `notify()` (same ordering as caseColumns/calculatedColumns); the dialog seeds local edit state from the `filters` prop on open via `useEffect([isOpen])`.
- Type model: `FilterCondition`/`FilterGroup` converted from `interface` stubs to discriminated `type` aliases with a unified `items: (FilterCondition | FilterGroup)[]` array (replacing parallel `conditions[]/groups[]`); added `FilterItem`, `FilterOperator = CaseOperator`, and `FILTER_OPERATORS = [...CASE_OPERATORS]`. `CaseOperator`/`CASE_OPERATORS` untouched.
- Module-level `_filterIdSeq` counter (never resets) generates `cond-N` / `grp-N` ids — collision-safe across dialog open/close cycles; deterministic for tests (no `Date.now()`).
- No backend work (story §6). No C# files modified; `dotnet test` baseline unaffected.
- Verification: tsc 0 errors · vitest 319 passed / 0 failed (316 baseline confirmed first) · i18n-check exit 0.
- Deferred items recorded in `deferred-work.md` (condition inputs → 10.6; drag-reorder → future; cascade-delete → 11.1/11.2; parseBuilderState normalization + server persistence → 11.2).

### File List

NEW:
- `web/src/components/query-builder/FilterConditionsDialog.tsx`

MODIFIED:
- `web/src/features/datasets/types/builderState.ts`
- `web/src/features/datasets/QueryBuilderCanvas.tsx`
- `web/src/lib/i18n/locales/en.json`
- `web/src/features/datasets/types/__tests__/builderState.test.ts`
- `_bmad-output/implementation-artifacts/deferred-work.md`
- `_bmad-output/implementation-artifacts/sprint-status.yaml`

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): FilterCondition/FilterGroup type expansion (id, kind, items[] unified), FilterItem/FilterOperator/FILTER_OPERATORS, EMPTY_BUILDER_STATE.filters update, FilterConditionsDialog modal with recursive FilterGroupContent, canvas "Filter" button + filters state/handleSaveFilters, 17 i18n keys, 3 unit tests. Status → ready-for-dev. | create-story |
| 2026-06-04 | 1.0     | Story implemented (dev-story): all 7 tasks complete. New FilterConditionsDialog.tsx (recursive FilterGroupContent, pure tree helpers, module-level id counter); builderState.ts filter types expanded; QueryBuilderCanvas wired (filters state + Filter button + handleSaveFilters); 17 i18n keys; 3 unit tests. Trimmed unused `useRef`/`FilterOperator`/`isRoot` from spec boilerplate to satisfy `noUnusedLocals`. Verified: tsc 0 errors, vitest 319/319, i18n-check exit 0. Status → review. | dev-story |
