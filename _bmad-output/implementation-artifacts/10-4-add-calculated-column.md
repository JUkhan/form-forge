# Story 10.4: Add Calculated Column (Expression)

Status: done

## Story

As a user building a Dataset,
I want to add a free-form SQL expression as a calculated column on any table node,
So that arithmetic or string operations can appear in the SELECT clause.

## Acceptance Criteria

**AC-1 — "Add Calculated Column" button creates a calculated column row on the node**
**Given** a table node on the canvas
**When** I click "+ Add Calculated"
**Then** a new calculated column is added to `builder_state.calculatedColumns[]` with an empty alias and empty expression, the `CalculatedColumnEditor` panel opens automatically, and a compact row for the new column appears at the bottom of the node (per FR-67 J-4 AC-1).

**AC-2 — Expression security validation (deferred to Story 11.1)**
**Given** the expression textarea
**When** I type an expression
**Then** the server applies the `ExpressionSecurityValidator.cs` three-layer check (keyword scan + wrap-parse + final SELECT-only) before including it in generated SQL (per AR-64 / FR-67 J-4 AC-2).
**Scope note:** `ExpressionSecurityValidator.cs` and `DatasetSqlGenerator.cs` do not exist yet. This story only stores the expression string in builder state correctly. The security validator is Story 11.1 scope.

**AC-3 — Empty alias blocks Save and shows a validation banner**
**Given** a calculated column with an empty alias
**When** the canvas renders
**Then** a validation banner appears: "A custom alias is required for every calculated column."
**And** `getCalculatedColumnAliasError(calculatedColumns)` SSOT gate returns the i18n key so Stories 11.2/11.3 can disable the Save/Preview buttons (per FR-67 J-4 AC-3).
**Scope note:** Save/Preview button-disabling is deferred to Story 11.2 (no Save button yet). The banner is wired now.

**AC-4 — SQL generator renders `({expression}) AS "alias"` (deferred to Story 11.1)**
**Given** a valid expression and alias
**When** the SQL generator runs (Story 11.1)
**Then** it inserts `({expression}) AS "alias"` in the SELECT clause (per FR-67 J-4 AC-4 / AR-66 step 6).
**Scope note:** Story 11.1 scope only. This story stores the expression; Story 11.1 produces the SQL.

**AC-5 — Calculated column state persists to builder_state**
**Given** I configure a calculated column (alias, expression)
**When** the `onChange` handler fires (on every change)
**Then** all data is stored in `builder_state.calculatedColumns[]` via `notify()` so Story 11.1's SQL generator can read the expression (per FR-67 J-4 AC-5).
**Scope note:** `builder_state` is not persisted to the server until Story 11.2.

---

## Tasks / Subtasks

### Task 1 — Frontend: Expand `CalculatedColumn` type and add gate function in `builderState.ts` (AC-3, AC-5)

Open `web/src/features/datasets/types/builderState.ts`.

**Step 1 — Replace the `CalculatedColumn` stub** (current 3-line `export interface CalculatedColumn { alias: string; expression: string; // Epic 10 Story 10.4 }`) with:

```typescript
// Story 10.4: expanded from the Epic 10 stub.
// id: client-side stable identifier for React keys + update/delete targeting.
// nodeId: table node this calculated column is attached to (display grouping;
//   expression may reference columns from any table per SQL conventions).
// alias: required (non-empty) — getCalculatedColumnAliasError gates Save (AC-3).
// expression: free-form SQL expression string; ExpressionSecurityValidator.cs
//   validates on save (Story 11.1). Do NOT pre-validate here — security is server-side.
export interface CalculatedColumn {
  id: string
  nodeId: string
  alias: string
  expression: string
}
```

**Step 2 — Add `getCalculatedColumnAliasError` after `getCaseColumnAliasError` (end of file):**

```typescript
// Story 10.4 AC-3: gate Save/Preview when any calculated column has an empty alias.
// Returns the i18n key when invalid, null when valid.
// SSOT — consumed by the canvas validation banner (Story 10.4) and by Stories
// 11.2/11.3 to disable Save/Preview buttons.
export function getCalculatedColumnAliasError(
  calculatedColumns: CalculatedColumn[],
): 'datasets.builder.validation.calculatedColumnRequiresAlias' | null {
  return calculatedColumns.some((c) => c.alias.trim() === '')
    ? 'datasets.builder.validation.calculatedColumnRequiresAlias'
    : null
}
```

**No changes to `BuilderState`, `EMPTY_BUILDER_STATE`** — `BuilderState.calculatedColumns: CalculatedColumn[]` and `EMPTY_BUILDER_STATE.calculatedColumns: []` are already correct stubs from Epic 8.

---

### Task 2 — Frontend: Create `CalculatedColumnEditor.tsx` (AC-1, AC-3, AC-5)

Create `web/src/components/query-builder/CalculatedColumnEditor.tsx`:

```typescript
import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  type CalculatedColumn,
} from '../../features/datasets/types/builderState'

interface CalculatedColumnEditorProps {
  calculatedColumn: CalculatedColumn
  onUpdate: (updated: CalculatedColumn) => void
  onClose: () => void
}

export function CalculatedColumnEditor({
  calculatedColumn,
  onUpdate,
  onClose,
}: CalculatedColumnEditorProps) {
  const { t } = useTranslation()
  const aliasError = calculatedColumn.alias !== '' && !isValidAlias(calculatedColumn.alias)
  const aliasErrorId = `calc-${calculatedColumn.id}-alias-error`
  const aliasRequiredId = `calc-${calculatedColumn.id}-alias-required`

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-80 overflow-y-auto rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      style={{ maxHeight: 'calc(100% - 2rem)' }}
      role="complementary"
      aria-label={t('datasets.builder.calculatedEditor.title')}
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">
          {t('datasets.builder.calculatedEditor.title')}
        </span>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('datasets.builder.calculatedEditor.closeAria')}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Expression */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.calculatedEditor.expressionLabel')}
          </p>
          <textarea
            value={calculatedColumn.expression}
            onChange={(e) => onUpdate({ ...calculatedColumn, expression: e.target.value })}
            aria-label={t('datasets.builder.calculatedEditor.expressionLabel')}
            placeholder={t('datasets.builder.calculatedEditor.expressionPlaceholder')}
            rows={3}
            className="nodrag nopan w-full resize-none rounded border border-border bg-background px-2 py-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
        </div>

        {/* Alias */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.calculatedEditor.aliasLabel')}
          </p>
          <input
            type="text"
            value={calculatedColumn.alias}
            onChange={(e) => onUpdate({ ...calculatedColumn, alias: e.target.value })}
            aria-label={t('datasets.builder.calculatedEditor.aliasLabel')}
            aria-invalid={aliasError || calculatedColumn.alias === ''}
            aria-describedby={
              calculatedColumn.alias === ''
                ? aliasRequiredId
                : aliasError
                  ? aliasErrorId
                  : undefined
            }
            placeholder="e.g. total_price"
            className={`h-7 w-full rounded border bg-background px-2 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
              aliasError ? 'border-destructive' : 'border-border'
            }`}
          />
          {calculatedColumn.alias === '' && (
            <p id={aliasRequiredId} className="text-xs text-destructive">
              {t('datasets.builder.calculatedEditor.aliasRequired')}
            </p>
          )}
          {aliasError && (
            <p id={aliasErrorId} className="text-xs text-destructive">
              {t('datasets.builder.node.aliasInvalidPattern')}
            </p>
          )}
        </div>
      </div>
    </div>
  )
}
```

**Critical notes:**
- `onUpdate` is called on every keystroke (live editing) — same pattern as `CaseColumnEditor` calling `onUpdate` on every field change.
- The `textarea` carries `nodrag nopan` so typing does not trigger canvas drag/pan.
- `resize-none` prevents the textarea from being resized (would break canvas layout).
- Alias validation reuses `isValidAlias` from `builderState.ts` (FR-57 rules — same as Story 10.2/10.3). Reuses existing `datasets.builder.node.aliasInvalidPattern` key for pattern message.
- `aria-describedby` points to `aliasRequiredId` when empty, to `aliasErrorId` when pattern-invalid (mutually exclusive), and to `undefined` when valid — mirrors the CaseColumnEditor pattern exactly.
- Unlike `CaseColumnEditor`, no `nodes` prop is needed — the expression textarea is free-form with no node/column selectors.
- `maxHeight: 'calc(100% - 2rem)'` + `overflow-y-auto` prevents overflow on small viewports.
- Panel is `z-50` (same as JoinInspector and CaseColumnEditor) and positioned `absolute right-4 top-4`.

---

### Task 3 — Frontend: Add 4 new contexts + calculated column section to `TableNode.tsx` (AC-1, AC-5)

Open `web/src/components/query-builder/TableNode.tsx`.

**Step 1 — Update imports.** Add `CalculatedColumn` to the builderState import:

```typescript
import {
  isValidAlias,
  type AggregateFunction,
  type CaseColumn,
  type CalculatedColumn,
  type TableNodeData,
  type TableSide,
} from '../../features/datasets/types/builderState'
```

**Step 2 — Export 4 new contexts** directly after `SelectCaseColumnContext` (after line 60):

```typescript
// Story 10.4: canvas provides the full calculatedColumns array so each TableNode
// can filter for its own (by nodeId) and render compact calculated column rows.
export const CalculatedColumnsContext = createContext<CalculatedColumn[]>([])

// Story 10.4: canvas provides add-calculated handler. Stable-ref via useCallback
// with deps [notify] — does not change on every render.
export const AddCalculatedColumnContext = createContext<(nodeId: string) => void>(() => {})

// Story 10.4: canvas provides delete-calculated handler. Same stable-ref posture.
export const DeleteCalculatedColumnContext = createContext<(calcId: string) => void>(() => {})

// Story 10.4: canvas provides select-calculated handler (opens CalculatedColumnEditor).
export const SelectCalculatedColumnContext = createContext<(calcId: string) => void>(() => {})
```

**Step 3 — Add context reads inside `TableNode`.** After the `onSelectCaseColumn = useContext(SelectCaseColumnContext)` and `nodeCaseColumns` lines, add:

```typescript
const calculatedColumns = useContext(CalculatedColumnsContext)
const onAddCalculatedColumn = useContext(AddCalculatedColumnContext)
const onDeleteCalculatedColumn = useContext(DeleteCalculatedColumnContext)
const onSelectCalculatedColumn = useContext(SelectCalculatedColumnContext)
// Filter to this node's calculated columns only
const nodeCalculatedColumns = calculatedColumns.filter((c) => c.nodeId === id)
```

**Step 4 — Add calculated column section and button** at the bottom of the node JSX, after the CASE columns section (after the `{/* Add Case button */}` `</div>`) and before the outer node `</div>`:

```tsx
{/* Calculated column rows — Story 10.4: one compact row per calculated column on this node */}
{nodeCalculatedColumns.map((cc) => (
  <div key={cc.id} className="flex items-center gap-2 border-t border-border px-3 py-1.5">
    <button
      type="button"
      onClick={() => onSelectCalculatedColumn(cc.id)}
      className="nodrag nopan min-w-0 flex-1 truncate rounded text-left text-xs text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
      aria-label={cc.alias || t('datasets.builder.node.calculatedColumnUntitled')}
    >
      <span className="font-mono">
        {cc.alias || t('datasets.builder.node.calculatedColumnUntitled')}
      </span>
    </button>
    <button
      type="button"
      onClick={() => onDeleteCalculatedColumn(cc.id)}
      aria-label={t('datasets.builder.node.deleteCalculatedColumnAria')}
      className="nodrag nopan flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
    >
      <X className="h-3 w-3" />
    </button>
  </div>
))}
{/* Add Calculated Column button — always visible; triggers add + auto-opens editor */}
<div className="border-t border-border px-3 py-1.5">
  <button
    type="button"
    onClick={() => onAddCalculatedColumn(id)}
    className="nodrag nopan w-full rounded text-left text-xs text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
  >
    {t('datasets.builder.node.addCalculatedColumnButton')}
  </button>
</div>
```

**Step 5 — Update the component-level comment** (after the Story 10.3 mention):

```typescript
// Story 10.4 adds calculated column rows + "Add Calculated Column" button
//   (CalculatedColumnsContext, AddCalculatedColumnContext,
//    DeleteCalculatedColumnContext, SelectCalculatedColumnContext).
```

---

### Task 4 — Frontend: Refactor unified panel selector + wire calculated columns in `QueryBuilderCanvas.tsx` (AC-1, AC-3, AC-5)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**ARCHITECTURAL CHANGE — Story 10.3 Dev Notes §5 mandated a unified panel selector for Story 10.4.**
The two separate selectors (`selectedEdge: JoinEdgeType | null` + `selectedCaseColumnId: string | null`) are REPLACED with a single discriminated-union state: `selectedPanel: SelectedPanel`. This eliminates N² mutual-exclusivity pairs and is the prescribed approach.

**Step 1 — Update the `TableNode` import.** Add the 4 new contexts:

```typescript
import {
  TableNode,
  TableSideChangeContext,
  ColumnCheckContext,
  ColumnAggregateContext,
  ColumnAliasContext,
  CaseColumnsContext,
  AddCaseColumnContext,
  DeleteCaseColumnContext,
  SelectCaseColumnContext,
  CalculatedColumnsContext,
  AddCalculatedColumnContext,
  DeleteCalculatedColumnContext,
  SelectCalculatedColumnContext,
  type TableNodeType,
} from '../../components/query-builder/TableNode'
```

**Step 2 — Add `CalculatedColumnEditor` import:**

```typescript
import { CalculatedColumnEditor } from '../../components/query-builder/CalculatedColumnEditor'
```

**Step 3 — Update the `builderState` import.** Add new types:

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
  type JoinEdgeState,
  type JoinType,
  type TableNodeData,
  type TableNodeState,
  type TableSide,
} from './types/builderState'
```

**Step 4 — Add the `SelectedPanel` type** as a local type at the top of `QueryBuilderCanvasInner` (or just before the component, after the imports):

```typescript
// Story 10.4: unified floating-panel selector — avoids N² mutual-exclusivity pairs.
// Story 10.3 Dev Notes §5 prescribed this for Story 10.4. Replaces the two separate
// `selectedEdge` and `selectedCaseColumnId` states from Story 10.3.
type SelectedPanel =
  | { type: 'join'; edge: JoinEdgeType }
  | { type: 'case'; id: string }
  | { type: 'calculated'; id: string }
  | null
```

**Step 5 — Replace state declarations.** Inside `QueryBuilderCanvasInner`, find and REMOVE:

```typescript
// Story 10.3: tracks which CASE column is open in CaseColumnEditor (null = closed).
const [selectedCaseColumnId, setSelectedCaseColumnId] = useState<string | null>(null)
// Story 9.4: the join edge currently shown in the JoinInspector (null = closed).
const [selectedEdge, setSelectedEdge] = useState<JoinEdgeType | null>(null)
```

REPLACE with the single unified state (add after the `caseIdCounterRef` line):

```typescript
// Story 10.4: replaces Story 10.3's separate selectedCaseColumnId + Story 9.4's selectedEdge.
// Unified selector eliminates mutual-exclusivity boilerplate (Story 10.3 Dev Notes §5).
const [selectedPanel, setSelectedPanel] = useState<SelectedPanel>(null)

// Story 10.4: calculatedColumns state + ref + counter, mirroring caseColumns pattern.
const [calculatedColumns, setCalculatedColumns] = useState<CalculatedColumn[]>(
  initialState.calculatedColumns,
)
const calculatedColumnsRef = useRef(calculatedColumns)
calculatedColumnsRef.current = calculatedColumns
const calcIdCounterRef = useRef(0)
```

**Step 6 — Update `onEdgeClick`.** Replace:

```typescript
const onEdgeClick = useCallback(
  (_event: React.MouseEvent, edge: JoinEdgeType) => {
    setSelectedEdge(edge)
    setSelectedCaseColumnId(null)
  },
  [],
)
```

With:

```typescript
const onEdgeClick = useCallback(
  (_event: React.MouseEvent, edge: JoinEdgeType) => {
    setSelectedPanel({ type: 'join', edge })
  },
  [],
)
```

**Step 7 — Update `onPaneClick`.** Replace:

```typescript
const onPaneClick = useCallback(() => {
  setSelectedEdge(null)
  setSelectedCaseColumnId(null)
}, [])
```

With:

```typescript
const onPaneClick = useCallback(() => {
  setSelectedPanel(null)
}, [])
```

**Step 8 — Update `handleJoinTypeChange`.** Replace the `setSelectedEdge` call at the end with:

```typescript
setSelectedPanel((prev) =>
  prev?.type === 'join' && prev.edge.id === edgeId && prev.edge.data
    ? { ...prev, edge: { ...prev.edge, data: { ...prev.edge.data, joinType } } }
    : prev,
)
```

(The `updatedEdges`, `setEdges(updatedEdges)`, and `notify(nodes, updatedEdges)` calls remain unchanged.)

**Step 9 — Update `handleAddCaseColumn`.** Replace the two separate `setSelectedCaseColumnId(newId)` + `setSelectedEdge(null)` calls at the end with:

```typescript
setSelectedPanel({ type: 'case', id: newId })
```

**Step 10 — Update `handleDeleteCaseColumn`.** Replace:

```typescript
setSelectedCaseColumnId((prev) => (prev === caseId ? null : prev))
```

With:

```typescript
setSelectedPanel((prev) => (prev?.type === 'case' && prev.id === caseId ? null : prev))
```

**Step 11 — Update `handleSelectCaseColumn`.** Replace:

```typescript
setSelectedCaseColumnId(caseId)
setSelectedEdge(null)
```

With:

```typescript
setSelectedPanel({ type: 'case', id: caseId })
```

**Step 12 — Add `handleAddCalculatedColumn`** after `handleUpdateCaseColumn`:

```typescript
// Story 10.4 AC-1/AC-5: create a new calculated column for the given node, auto-open
// the editor, and emit via notify(). Reads refs for stable-ref posture.
const handleAddCalculatedColumn = useCallback(
  (nodeId: string) => {
    const newId = `calc-${nodeId}-${++calcIdCounterRef.current}`
    const newCalc: CalculatedColumn = {
      id: newId,
      nodeId,
      alias: '',
      expression: '',
    }
    const updated = [...calculatedColumnsRef.current, newCalc]
    setCalculatedColumns(updated)
    extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updated }
    notify(nodesRef.current, edgesRef.current)
    setSelectedPanel({ type: 'calculated', id: newId })
  },
  [notify],
)

// Story 10.4 AC-5: delete a calculated column by id and emit via notify().
const handleDeleteCalculatedColumn = useCallback(
  (calcId: string) => {
    const updated = calculatedColumnsRef.current.filter((c) => c.id !== calcId)
    setCalculatedColumns(updated)
    extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updated }
    notify(nodesRef.current, edgesRef.current)
    setSelectedPanel((prev) => (prev?.type === 'calculated' && prev.id === calcId ? null : prev))
  },
  [notify],
)

// Story 10.4: open the CalculatedColumnEditor for the given calculated column id.
const handleSelectCalculatedColumn = useCallback((calcId: string) => {
  setSelectedPanel({ type: 'calculated', id: calcId })
}, [])

// Story 10.4 AC-5: update a calculated column in-place and emit via notify().
// Called by CalculatedColumnEditor on every field change (live editing).
const handleUpdateCalculatedColumn = useCallback(
  (updated: CalculatedColumn) => {
    const updatedAll = calculatedColumnsRef.current.map((c) =>
      c.id === updated.id ? updated : c,
    )
    setCalculatedColumns(updatedAll)
    extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updatedAll }
    notify(nodesRef.current, edgesRef.current)
  },
  [notify],
)
```

**Step 13 — Update `handleNodesChange`.** Replace the entire `if (removedIds.size > 0)` block content with:

```typescript
if (removedIds.size > 0) {
  const survivingEdges = edges.filter(
    (e) => !removedIds.has(e.source) && !removedIds.has(e.target),
  )
  setEdges(survivingEdges)
  const survivingNodes = nodes.filter((n) => !removedIds.has(n.id))

  // Cascade CASE column removal (Story 10.3) — extraStateRef MUST be updated before notify().
  const survivingCaseColumns = caseColumnsRef.current.filter(
    (c) => !removedIds.has(c.nodeId),
  )
  if (survivingCaseColumns.length !== caseColumnsRef.current.length) {
    setCaseColumns(survivingCaseColumns)
    extraStateRef.current = { ...extraStateRef.current, caseColumns: survivingCaseColumns }
  }

  // Story 10.4: cascade calculated column removal — same ordering requirement.
  const survivingCalcColumns = calculatedColumnsRef.current.filter(
    (c) => !removedIds.has(c.nodeId),
  )
  if (survivingCalcColumns.length !== calculatedColumnsRef.current.length) {
    setCalculatedColumns(survivingCalcColumns)
    extraStateRef.current = { ...extraStateRef.current, calculatedColumns: survivingCalcColumns }
  }

  notify(survivingNodes, survivingEdges)

  // Close the floating panel if it references a removed node/edge.
  // Uses refs (pre-update values) to check nodeId membership — correct because
  // removedIds captures the deletion batch and refs hold pre-batch state.
  setSelectedPanel((prev) => {
    if (!prev) return null
    if (prev.type === 'join') {
      return removedIds.has(prev.edge.source) || removedIds.has(prev.edge.target)
        ? null
        : prev
    }
    if (prev.type === 'case') {
      const target = caseColumnsRef.current.find((c) => c.id === prev.id)
      return target && removedIds.has(target.nodeId) ? null : prev
    }
    if (prev.type === 'calculated') {
      const target = calculatedColumnsRef.current.find((c) => c.id === prev.id)
      return target && removedIds.has(target.nodeId) ? null : prev
    }
    return null
  })
}
```

**Step 14 — Update `handleEdgesChange`.** Replace:

```typescript
setSelectedEdge((prev) => (prev && removedEdgeIds.has(prev.id) ? null : prev))
```

With:

```typescript
setSelectedPanel((prev) =>
  prev?.type === 'join' && removedEdgeIds.has(prev.edge.id) ? null : prev,
)
```

**Step 15 — Add `calculatedColumnAliasError` to the validation computation** (after `caseColumnAliasError` derivation):

```typescript
// Story 10.4 AC-3: gate Save/Preview when any calculated column has an empty alias.
const calculatedColumnAliasError = getCalculatedColumnAliasError(calculatedColumns)
```

**Step 16 — Derive selectedCaseColumn using the unified panel** (replace the old derivation):

```typescript
// Unified panel derivations — null if id is stale after deletion
const selectedCaseColumn =
  selectedPanel?.type === 'case'
    ? (caseColumns.find((c) => c.id === selectedPanel.id) ?? null)
    : null

const selectedCalculatedColumn =
  selectedPanel?.type === 'calculated'
    ? (calculatedColumns.find((c) => c.id === selectedPanel.id) ?? null)
    : null
```

**Step 17 — Extend the provider stack** with 4 new providers around the existing structure:

```tsx
<CaseColumnsContext.Provider value={caseColumns}>
  <AddCaseColumnContext.Provider value={handleAddCaseColumn}>
    <DeleteCaseColumnContext.Provider value={handleDeleteCaseColumn}>
      <SelectCaseColumnContext.Provider value={handleSelectCaseColumn}>
        <CalculatedColumnsContext.Provider value={calculatedColumns}>
          <AddCalculatedColumnContext.Provider value={handleAddCalculatedColumn}>
            <DeleteCalculatedColumnContext.Provider value={handleDeleteCalculatedColumn}>
              <SelectCalculatedColumnContext.Provider value={handleSelectCalculatedColumn}>
                <ReactFlow ... >
                  ...
                </ReactFlow>
              </SelectCalculatedColumnContext.Provider>
            </DeleteCalculatedColumnContext.Provider>
          </AddCalculatedColumnContext.Provider>
        </CalculatedColumnsContext.Provider>
      </SelectCaseColumnContext.Provider>
    </DeleteCaseColumnContext.Provider>
  </AddCaseColumnContext.Provider>
</CaseColumnsContext.Provider>
```

**Step 18 — Update validation banner** to include calculated column alias error:

```tsx
{(leftTableError || columnSelectionError || caseColumnAliasError || calculatedColumnAliasError) && (
  <div className="...">
    {/* existing leftTableError, columnSelectionError, caseColumnAliasError banners unchanged */}
    {calculatedColumnAliasError && (
      <div
        role="alert"
        className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
      >
        {t('datasets.builder.validation.calculatedColumnRequiresAlias')}
      </div>
    )}
  </div>
)}
```

**Step 19 — Replace the panel renders.** Replace:

```tsx
{selectedEdge && (
  <JoinInspector
    edge={selectedEdge}
    nodes={nodes}
    onJoinTypeChange={handleJoinTypeChange}
    onClose={() => setSelectedEdge(null)}
  />
)}
{selectedCaseColumn && (
  <CaseColumnEditor
    caseColumn={selectedCaseColumn}
    nodes={nodes}
    onUpdate={handleUpdateCaseColumn}
    onClose={() => setSelectedCaseColumnId(null)}
  />
)}
```

With:

```tsx
{selectedPanel?.type === 'join' && (
  <JoinInspector
    edge={selectedPanel.edge}
    nodes={nodes}
    onJoinTypeChange={handleJoinTypeChange}
    onClose={() => setSelectedPanel(null)}
  />
)}
{selectedCaseColumn && (
  <CaseColumnEditor
    caseColumn={selectedCaseColumn}
    nodes={nodes}
    onUpdate={handleUpdateCaseColumn}
    onClose={() => setSelectedPanel(null)}
  />
)}
{selectedCalculatedColumn && (
  <CalculatedColumnEditor
    calculatedColumn={selectedCalculatedColumn}
    onUpdate={handleUpdateCalculatedColumn}
    onClose={() => setSelectedPanel(null)}
  />
)}
```

---

### Task 5 — Frontend: Add i18n keys to `en.json` (AC-1, AC-3)

Open `web/src/lib/i18n/locales/en.json`.

**Step 1 — Extend `datasets.builder.node`** (after `deleteCaseColumnAria`):

```json
"node": {
  "left": "Left",
  "right": "Right",
  "sideGroupAria": "Table side designation",
  "aliasInvalidPattern": "Use lowercase letters, digits, and underscores; must start with a letter or underscore (max 63 chars).",
  "addCaseButton": "+ Add Case",
  "caseColumnUntitled": "(untitled)",
  "deleteCaseColumnAria": "Delete case column",
  "addCalculatedColumnButton": "+ Add Calculated",
  "calculatedColumnUntitled": "(untitled)",
  "deleteCalculatedColumnAria": "Delete calculated column"
}
```

**Step 2 — Extend `datasets.builder.validation`** (after `caseColumnRequiresAlias`):

```json
"validation": {
  "noLeftTable": "Designate one table as the left (FROM) table.",
  "noColumnsSelected": "Select at least one column.",
  "caseColumnRequiresAlias": "A custom alias is required for every CASE column.",
  "calculatedColumnRequiresAlias": "A custom alias is required for every calculated column."
}
```

**Step 3 — Add `datasets.builder.calculatedEditor` section** (after `datasets.builder.caseEditor`):

```json
"calculatedEditor": {
  "title": "Calculated Column",
  "closeAria": "Close calculated column editor",
  "expressionLabel": "Expression",
  "expressionPlaceholder": "e.g. price * quantity",
  "aliasLabel": "Alias",
  "aliasRequired": "Alias is required."
}
```

Total new keys: 10. All are referenced by static `t('…')` literals in `TableNode.tsx`, `QueryBuilderCanvas.tsx`, and `CalculatedColumnEditor.tsx` → **i18n-check must exit 0**.

Do **not** add a new key for alias pattern validation — it reuses the existing `datasets.builder.node.aliasInvalidPattern`.

---

### Task 6 — Frontend: Unit tests for `getCalculatedColumnAliasError` (AC-3)

Open `web/src/features/datasets/types/__tests__/builderState.test.ts`.

**Step 1 — Update the import** to add `getCalculatedColumnAliasError` and `CalculatedColumn`:

```typescript
import {
  getColumnSelectionValidationError,
  getLeftTableValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
  isValidAlias,
} from '../builderState'
import type { CalculatedColumn, CaseColumn, ColumnSelection, TableNodeState, TableSide } from '../builderState'
```

**Step 2 — Add a helper** after `caseCol`:

```typescript
function calcCol(alias: string): CalculatedColumn {
  return { id: 'cc1', nodeId: 'n1', alias, expression: 'price * qty' }
}
```

**Step 3 — Add `describe('getCalculatedColumnAliasError', ...)` block** after the `getCaseColumnAliasError` describe:

```typescript
describe('getCalculatedColumnAliasError', () => {
  it('returns null for an empty calculatedColumns array (nothing to validate)', () => {
    expect(getCalculatedColumnAliasError([])).toBeNull()
  })

  it('returns null when all calculated columns have non-empty aliases', () => {
    expect(
      getCalculatedColumnAliasError([calcCol('total_price'), calcCol('discount_amount')]),
    ).toBeNull()
  })

  it('returns the i18n key when one calculated column has an empty alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('total_price'), calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns the i18n key when all calculated columns have empty aliases', () => {
    expect(getCalculatedColumnAliasError([calcCol(''), calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns the i18n key for a single calculated column with an empty alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns null for a single calculated column with a valid alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('my_calc')])).toBeNull()
  })
})
```

Expected: **316 passed / 0 failed** (310 baseline + 6 new). Verify the current baseline first with `npx vitest run` before writing tests.

---

### Task 7 — Frontend: Add deferred-work entry

Add to `_bmad-output/implementation-artifacts/deferred-work.md` at the top (after the file heading, before the first `## Deferred from:` section):

```markdown
## Deferred from: implementation of 10-4-add-calculated-column

- **Save/Preview blocking on empty calculated column alias (AC-3) deferred to Story 11.2** — `getCalculatedColumnAliasError(calculatedColumns)` is the SSOT gate; 11.2 must call it alongside the other validation gates and set `disabled` on the Save button. The banner is wired now. [`builderState.ts`, `QueryBuilderCanvas.tsx`]
- **ExpressionSecurityValidator.cs (AC-2) deferred to Story 11.1** — the three-layer check (keyword scan + wrap-parse + final SELECT-only, per AR-64) does not exist yet. Story 11.1 implements it for both CASE and calculated columns. [`DatasetSqlGenerator.cs`, `ExpressionSecurityValidator.cs`]
- **BuilderStateDto.cs CalculatedColumn shape deferred to Story 11.1** — the C# DTO mirror does not exist yet. TypeScript `CalculatedColumn` (id, nodeId, alias, expression) is the cross-layer contract; any deviation in C# is a breaking change per Decision 6.11. [`DatasetSqlGenerator.cs`]
- **SQL generation of `({expression}) AS "alias"` (AC-4) deferred to Story 11.1** — Story 11.1's `DatasetSqlGenerator.cs` emits the calculated column in the SELECT list. [`DatasetSqlGenerator.cs`]
```

---

### Task 8 — Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → 316 passed / 0 failed (310 baseline verified before writing tests + 6 new `getCalculatedColumnAliasError` tests)
- [x] `cd web && node scripts/i18n-check.mjs` → exit 0 (all 10 new keys referenced by static `t()` literals; 89 pre-existing orphaned warnings unchanged, non-fatal)
- [x] Manual smoke (AC-1): verified by code inspection — `handleAddCalculatedColumn` creates `CalculatedColumn` with `id`/`nodeId`/`alias:''`/`expression:''`, appends to `calculatedColumnsRef.current`, calls `setCalculatedColumns`, updates `extraStateRef`, calls `notify`, then `setSelectedPanel({ type: 'calculated', id: newId })`. `TableNode` renders the compact row because `nodeCalculatedColumns.filter((c) => c.nodeId === id)` includes the new column; the editor opens because `selectedCalculatedColumn` resolves the new id. Editor opens with empty alias + empty expression textarea (both initialized to `''`). Mirrors the shipped/tested Story 10.3 CASE flow exactly.
- [x] Manual smoke (AC-3, alias banner): verified — `getCalculatedColumnAliasError([{ alias: '' }])` returns `'datasets.builder.validation.calculatedColumnRequiresAlias'` (unit-tested); banner condition `calculatedColumnAliasError` is truthy → banner renders with that i18n string.
- [x] Manual smoke (AC-5, state persists): verified — `handleUpdateCalculatedColumn` fires on every keystroke (textarea/input `onChange` → `onUpdate`), writes to `calculatedColumnsRef.current`, updates `extraStateRef.current.calculatedColumns` before `notify(...)` → parent `onChange` receives the full `BuilderState` including updated `calculatedColumns`.
- [x] Manual smoke (cascade node delete): verified — `handleNodesChange` computes `survivingCalcColumns` and updates `extraStateRef.current.calculatedColumns` before `notify(survivingNodes, survivingEdges)`; the unified `setSelectedPanel` closure returns `null` when `prev.type === 'calculated'` and the column's `nodeId` was removed.
- [x] Manual smoke (unified panel): verified — `setSelectedPanel` is the single selector; opening Join Inspector (`onEdgeClick` → `{type:'join'}`), then Add Calculated (`{type:'calculated'}`) replaces it (Join Inspector closes), and vice versa; `onPaneClick` sets `null`, closing all panels. Discriminated-union render guards (`selectedPanel?.type === 'join'`, `selectedCaseColumn`, `selectedCalculatedColumn`) are mutually exclusive by construction.
- [x] Backend baseline: no backend work in this story — `DatasetSqlGenerator.cs`, `ExpressionSecurityValidator.cs`, and `BuilderStateDto.cs CalculatedColumn` are deferred to Story 11.1; no API/EF changes made.

### Review Findings

_Code review 2026-06-04 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). Auditor verdict: all in-scope ACs (1, 3, 5) satisfied; AC-2/AC-4 correctly deferred to 11.1; all 10 i18n keys + 6 unit tests present and correct; unified `selectedPanel` refactor implemented as prescribed. No in-scope correctness defect is reachable in the current epic state._

- [x] [Review][Defer] `parseBuilderState` does not default new array fields → white-screen crash on a partial `builder_state` blob [web/src/features/datasets/types/builderState.ts] — A blob with valid `nodes`/`edges` arrays but missing `calculatedColumns` passes the parse guard and yields `calculatedColumns: undefined`; `TableNode`'s `calculatedColumns.filter(...)` and the canvas's `getCalculatedColumnAliasError(...)` (`.some`) then throw on first render. Pre-existing `parseBuilderState` gap (already deferred for `caseColumns` in 10.1 W2 / 10.2), now a hard crash for `calculatedColumns`. **Deferred (user decision 2026-06-04):** not reachable until Story 11.2 wires `builder_state` persistence/restore, and the root-cause normalization (default all array + per-column fields) is owned by 11.2 — fix there alongside the restore path.
- [x] [Review][Defer] `calcIdCounterRef` starts at 0 → generated id collides with restored ids after a reload [web/src/features/datasets/QueryBuilderCanvas.tsx] — deferred, not reachable until Story 11.2 wires `builder_state` persistence/restore. Mirrors the identical latent `caseIdCounterRef` pattern from Story 10.3; 11.2 must seed BOTH counters from `initialState`.
- [x] [Review][Defer] No alias-uniqueness validation across calculated / CASE / selected columns [web/src/features/datasets/types/builderState.ts, QueryBuilderCanvas.tsx] — deferred, cross-cutting gap out of this story's scope (AC-3 covers non-empty only). Duplicate output aliases (`AS total` twice) belong to SQL-generation/save validation in Story 11.1/11.2; server `SafeIdentifier`/SQL gen is authoritative.

_Dismissed as noise (4): alias gate checks empty-only not `isValidAlias` (by design per AC-3, mirrors `getCaseColumnAliasError`; pattern-blocking already deferred to 11.2); `aria-invalid`+"required" shown on a fresh empty column (by design, mirrors shipped `CaseColumnEditor`, spec-prescribed); whitespace-only alias editor/banner message asymmetry (Low UX nit, Save still correctly blocked); stable-ref dep arrays retained on `handleJoinTypeChange`/`handleNodesChange`/`handleEdgesChange` (pre-existing from 9.4/10.3, matches the spec's literal Step 6/8/13/14 instructions, not a regression — new calc handlers correctly use `[notify]`)._

---

## Dev Notes

### §1 — Current State Left by Story 10.3

`builderState.ts`:
- `CalculatedColumn` is a 3-line stub: `{ alias: string; expression: string; // Epic 10 Story 10.4 }`.
- `BuilderState.calculatedColumns: CalculatedColumn[]` and `EMPTY_BUILDER_STATE.calculatedColumns: []` are correct — no changes needed.
- `CaseColumn`, `getCaseColumnAliasError`, and `CASE_OPERATORS`/`CaseWhen` are fully implemented.

`TableNode.tsx`:
- Has CASE column rows + "Add Case" button + 4 CASE contexts.
- No calculated column UI. Exports need 4 new contexts.
- `X` from lucide-react already imported. No new icon imports needed.

`QueryBuilderCanvas.tsx`:
- Has `selectedEdge: JoinEdgeType | null` + `selectedCaseColumnId: string | null` — both are REPLACED by `selectedPanel: SelectedPanel` in this story.
- Has `caseColumns` state + `caseColumnsRef` + `caseIdCounterRef` — mirror the same pattern for `calculatedColumns`.
- `extraStateRef` already initializes `calculatedColumns: initialState.calculatedColumns` at mount.
- Story 10.3 Dev Notes §5 explicitly mandated the unified `selectedPanel` refactor for Story 10.4.

`CaseColumnEditor.tsx`: fully implemented, do NOT touch.

### §2 — Why `CalculatedColumn` Needs `id` and `nodeId`

The Epic 8 stub `{ alias, expression }` has no `id` — this causes React key collisions if two calculated columns coexist on the same node (identical keys in `map()`). It also prevents the `handleDeleteCalculatedColumn` and `handleUpdateCalculatedColumn` handlers from targeting the correct entry (they match by `id`, not by `alias`).

Adding `id` (stable, client-generated via `calcIdCounterRef`) and `nodeId` (for display grouping and cascade deletion on node removal) mirrors the `CaseColumn` pattern exactly. This is not premature abstraction — both fields are immediately required by the handlers.

### §3 — Unified Panel Selector Rationale

Without the unified selector, Story 10.4 would add:
- `handleAddCalculatedColumn` → `setSelectedEdge(null); setSelectedCaseColumnId(null)`
- `handleSelectCalculatedColumn` → `setSelectedEdge(null); setSelectedCaseColumnId(null)`
- Every existing mutual-exclusivity call site → update to also null `selectedCalculatedColumnId`

That's 4 existing call sites × 1 new setter = 4 extra lines of boilerplate, and 2 new call sites × 2 existing nullifications = 4 more lines. Story 10.3 Dev Notes §5 prescribed the unified approach specifically to avoid this.

The `SelectedPanel` discriminated union:
```typescript
type SelectedPanel =
  | { type: 'join'; edge: JoinEdgeType }
  | { type: 'case'; id: string }
  | { type: 'calculated'; id: string }
  | null
```

One `setSelectedPanel(...)` call replaces all mutual-exclusivity boilerplate. Story 11 can add further panel types without touching any existing call sites.

### §4 — Stable-Ref Pattern for Calculated Column Handlers

All four handlers read from `calculatedColumnsRef` / `nodesRef` / `edgesRef`, not closure state. Their `useCallback` deps are `[notify]` or `[]`. This ensures `AddCalculatedColumnContext.Provider value`, `DeleteCalculatedColumnContext.Provider value`, and `SelectCalculatedColumnContext.Provider value` stay referentially stable, preserving `TableNode`'s `memo()` optimization.

`CalculatedColumnsContext.Provider value={calculatedColumns}` IS state — it changes whenever any calculated column is added/deleted/updated. All `TableNode` instances re-render then. Same tradeoff as `CaseColumnsContext` (acceptable for small canvases).

### §5 — `extraStateRef` Update Ordering in `handleNodesChange`

Both `extraStateRef.current.caseColumns` AND `extraStateRef.current.calculatedColumns` MUST be updated **before** calling `notify(survivingNodes, survivingEdges)`. The `notify` function spreads `extraStateRef.current`. If either update happens after `notify()`, the parent receives one stale cycle of data.

The `setSelectedPanel` update runs after `notify()` — this is fine because it's UI-only state and does not affect the `BuilderState` emitted to the parent.

### §6 — No Backend Work

- `DatasetSqlGenerator.cs` does not exist yet (Story 11.1).
- `ExpressionSecurityValidator.cs` does not exist yet (Story 11.1).
- `BuilderStateDto.cs` with `CalculatedColumn` does not exist yet (Story 11.1).
- No API calls, no EF migration. Server persistence is Story 11.2.
- Backend baseline: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures from `deferred-work.md`).

### §7 — Files to Create / Modify

```
MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — expand CalculatedColumn stub: add id, nodeId fields
    — add getCalculatedColumnAliasError() SSOT gate

  web/src/features/datasets/types/__tests__/builderState.test.ts
    — add getCalculatedColumnAliasError + CalculatedColumn type imports
    — add calcCol() helper
    — add 6-case describe block for getCalculatedColumnAliasError

  web/src/components/query-builder/TableNode.tsx
    — add CalculatedColumn to builderState import
    — export CalculatedColumnsContext, AddCalculatedColumnContext,
        DeleteCalculatedColumnContext, SelectCalculatedColumnContext
    — add context reads + nodeCalculatedColumns filter in TableNode
    — add calculated column compact rows + "Add Calculated Column" button
    — update component comment

  web/src/features/datasets/QueryBuilderCanvas.tsx
    — imports: 4 new contexts from TableNode, CalculatedColumnEditor,
        getCalculatedColumnAliasError, CalculatedColumn from builderState
    — REFACTOR: replace selectedEdge + selectedCaseColumnId with
        selectedPanel: SelectedPanel (unified discriminated union)
    — state: calculatedColumns (useState), calculatedColumnsRef (useRef),
        calcIdCounterRef (useRef)
    — handlers: handleAddCalculatedColumn, handleDeleteCalculatedColumn,
        handleSelectCalculatedColumn, handleUpdateCalculatedColumn
    — REFACTOR: onEdgeClick, onPaneClick, handleJoinTypeChange,
        handleAddCaseColumn, handleDeleteCaseColumn, handleSelectCaseColumn
        — all updated to use setSelectedPanel
    — handleNodesChange: cascade calculated column removal before notify();
        unified setSelectedPanel for panel closure
    — handleEdgesChange: updated to use setSelectedPanel
    — validation: add calculatedColumnAliasError gate
    — provider stack: 4 new providers wrapping existing providers
    — validation banner: add calculatedColumnAliasError branch
    — panel renders: update all 3 panels to use selectedPanel

  web/src/lib/i18n/locales/en.json
    — datasets.builder.node: 3 new keys
    — datasets.builder.validation: 1 new key
    — datasets.builder.calculatedEditor: 6 new keys

NEW:
  web/src/components/query-builder/CalculatedColumnEditor.tsx
    — floating panel: expression textarea + alias input + close button
    — live editing via onUpdate prop

DOC:
  _bmad-output/implementation-artifacts/deferred-work.md
    — add 10-4 deferral entries (AC-2 backend validator, AC-4 SQL gen, DTO, save-blocking)
```

### §8 — Git / Recent-Work Patterns (from Stories 10.1–10.3)

- One context per action type, stable-ref via `useCallback` with minimal deps — follow exactly.
- `nodrag nopan` on EVERY interactive element inside a node or floating panel.
- No hardcoded colors — use theme tokens (`bg-background`, `border-border`, `text-destructive`).
- New i18n keys must have a matching static `t('key.name')` literal call (i18n-lint fatal).
- New `describe` blocks appended to `builderState.test.ts` without modifying existing tests.
- All handlers mirror the `handleColumnCheck` stable-ref posture (read refs, deps `[notify]` only or `[]`).
- Counter refs (`caseIdCounterRef`, `calcIdCounterRef`) are preferred over `Date.now()` for IDs — avoids collision under rapid successive clicks and is not non-deterministic.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 10.4 ACs (FR-67 J-4 AC-1..AC-5)]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-64 ExpressionSecurityValidator (Story 11.1)]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-66 DatasetSqlGenerator step 6 (Story 11.1)]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-67 builder_state CalculatedColumn contract]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-68 Decision 6.12 "Add Calculated Column button on TableNode"]
- [Source: `web/src/features/datasets/types/builderState.ts` — CalculatedColumn stub, EMPTY_BUILDER_STATE]
- [Source: `web/src/components/query-builder/TableNode.tsx` — current node structure (post-10.3)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — current canvas state (post-10.3)]
- [Source: `web/src/components/query-builder/CaseColumnEditor.tsx` — floating panel pattern to mirror]
- [Source: `_bmad-output/implementation-artifacts/10-3-add-case-column.md §5, §9` — unified panel selector mandate + stable-ref pattern]
- [Source: Memory — @xyflow/react v12 typing gotchas: data must be type aliases not interfaces (CalculatedColumn is top-level, unaffected)]
- [Source: Memory — i18n-lint failure RESOLVED; a red i18n-lint is now a real regression]
- [Source: Memory — Validators registered explicitly (no scan); note for future backend stories]
- [Source: Memory — pgsqlparser 1.0.0 for SQL parsing (ExpressionSecurityValidator, Story 11.1)]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] — BMad dev-story workflow (implementation)

### Debug Log References

- Baseline `npx vitest run` before writing tests: **310 passed / 0 failed** (27 files).
- Post-implementation `npx tsc -b --noEmit`: **0 errors**.
- Post-implementation `npx vitest run`: **316 passed / 0 failed** (310 + 6 new `getCalculatedColumnAliasError` cases).
- `node scripts/i18n-check.mjs`: **exit 0** (10 new keys all referenced by static `t()`; 89 pre-existing orphaned-key warnings unchanged, non-fatal).
- eslint on changed files reports only **pre-existing rule classes**: `react-hooks/refs` on the ref-mirror pattern in `QueryBuilderCanvas.tsx` (committed Story 10.3 file already fails this with 3 errors — my `calculatedColumnsRef.current = calculatedColumns` is one more instance of the prescribed pattern) and `react-refresh/only-export-components` on `TableNode.tsx` context exports (the file already exported 8 contexts; my 4 new ones add to the same pre-existing class). New `CalculatedColumnEditor.tsx`, `builderState.ts`, and the test file are lint-clean. eslint is not a Task 8 gate.

### Completion Notes List

- **AC-1 (Add Calculated Column button):** "+ Add Calculated" button added to every `TableNode`; click → `handleAddCalculatedColumn` appends an empty calculated column to `builder_state.calculatedColumns[]` and auto-opens `CalculatedColumnEditor` via the unified `selectedPanel`.
- **AC-2 (Expression security validation):** Deferred to Story 11.1 per scope note — this story stores the expression string in builder state only; `ExpressionSecurityValidator.cs`/`DatasetSqlGenerator.cs` do not exist yet. Recorded in `deferred-work.md`.
- **AC-3 (Empty alias blocks Save + banner):** `getCalculatedColumnAliasError(calculatedColumns)` SSOT gate added to `builderState.ts`; canvas validation banner wired with i18n key `datasets.builder.validation.calculatedColumnRequiresAlias`. Save/Preview button-disabling deferred to Story 11.2 (no Save button exists yet) — recorded in `deferred-work.md`.
- **AC-4 (SQL `({expression}) AS "alias"`):** Deferred to Story 11.1 per scope note. Recorded in `deferred-work.md`.
- **AC-5 (State persists to builder_state):** All four handlers (`handleAddCalculatedColumn`, `handleDeleteCalculatedColumn`, `handleSelectCalculatedColumn`, `handleUpdateCalculatedColumn`) update `extraStateRef.current.calculatedColumns` before `notify()`, so the parent's `onChange` always receives the current `calculatedColumns[]`. `onUpdate` fires on every keystroke (live editing).
- **Architectural refactor (Story 10.3 Dev Notes §5 mandate):** Replaced the two separate `selectedEdge` + `selectedCaseColumnId` states with a single `selectedPanel: SelectedPanel` discriminated union (`join` | `case` | `calculated` | `null`). All mutual-exclusivity call sites (`onEdgeClick`, `onPaneClick`, `handleJoinTypeChange`, `handleAddCaseColumn`, `handleDeleteCaseColumn`, `handleSelectCaseColumn`, `handleNodesChange`, `handleEdgesChange`) updated to use the single `setSelectedPanel`.
- **Cascade delete:** `handleNodesChange` cascades calculated column removal (mirrors CASE column cascade); `extraStateRef` updated before `notify()`; the unified panel closes if it references a removed node/edge.
- **Type expansion:** `CalculatedColumn` expanded from the Epic 8 `{ alias, expression }` stub to `{ id, nodeId, alias, expression }` — `id` for stable React keys + update/delete targeting, `nodeId` for display grouping + cascade deletion. Mirrors `CaseColumn` exactly.
- **No backend work** in this story (per Dev Notes §6). Backend baseline unchanged.

### File List

- `web/src/features/datasets/types/builderState.ts` (modified) — expanded `CalculatedColumn` (added `id`, `nodeId`); added `getCalculatedColumnAliasError()` SSOT gate.
- `web/src/components/query-builder/CalculatedColumnEditor.tsx` (new) — floating panel: expression textarea + alias input + close button; live editing via `onUpdate`.
- `web/src/components/query-builder/TableNode.tsx` (modified) — added `CalculatedColumn` import; exported 4 new contexts (`CalculatedColumnsContext`, `AddCalculatedColumnContext`, `DeleteCalculatedColumnContext`, `SelectCalculatedColumnContext`); added context reads + `nodeCalculatedColumns` filter; added compact calculated-column rows + "Add Calculated Column" button; updated component comment.
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (modified) — imported 4 new contexts + `CalculatedColumnEditor` + `getCalculatedColumnAliasError` + `CalculatedColumn`; refactored `selectedEdge`/`selectedCaseColumnId` → unified `selectedPanel: SelectedPanel`; added `calculatedColumns` state/ref/counter; added 4 calculated-column handlers; updated all mutual-exclusivity call sites; cascade calculated-column removal in `handleNodesChange`; `calculatedColumnAliasError` validation gate + banner branch; 4 new providers; 3 panel renders updated.
- `web/src/lib/i18n/locales/en.json` (modified) — 3 new `datasets.builder.node` keys, 1 new `datasets.builder.validation` key, new `datasets.builder.calculatedEditor` section (6 keys).
- `web/src/features/datasets/types/__tests__/builderState.test.ts` (modified) — added `getCalculatedColumnAliasError` + `CalculatedColumn` imports, `calcCol()` helper, 6-case `describe` block.
- `_bmad-output/implementation-artifacts/deferred-work.md` (modified) — added 10-4 deferral entries (AC-2 validator, AC-4 SQL gen, DTO, save-blocking).

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): CalculatedColumn type expansion (id, nodeId), CalculatedColumnEditor panel, 4 contexts (CalculatedColumnsContext/Add/Delete/Select), unified SelectedPanel refactor (replaces selectedEdge + selectedCaseColumnId), stable-ref handlers, cascade node-delete, getCalculatedColumnAliasError SSOT, validation banner, 10 i18n keys, 6 unit tests. Status → ready-for-dev. | create-story |
| 2026-06-04 | 1.0     | Story implemented (dev-story): all 8 tasks complete. CalculatedColumn expanded (id, nodeId); CalculatedColumnEditor.tsx created; 4 contexts + calculated-column rows + "Add Calculated" button in TableNode; unified selectedPanel refactor in QueryBuilderCanvas; 4 handlers + cascade delete + validation banner; 10 i18n keys; 6 new unit tests. tsc 0 errors, vitest 316 passed/0 failed, i18n-check exit 0. Status → review. | dev-story |
