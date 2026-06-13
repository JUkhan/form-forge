# Story 10.3: Add CASE Column (CASE/WHEN Derived Column)

Status: done

## Story

As a user building a Dataset,
I want to add a CASE/WHEN expression as a derived column on any table node,
So that conditional logic can be expressed in the SELECT clause without writing raw SQL.

## Acceptance Criteria

**AC-1 — "Add Case" button creates a CASE column row on the node**
**Given** a table node on the canvas
**When** I click "+ Add Case"
**Then** a new CASE column is added to `builder_state.caseColumns[]` with an empty alias and one default WHEN arm, the `CaseColumnEditor` panel opens automatically, and a compact row for the new CASE column appears at the bottom of the node (per FR-67 J-3 AC-1).

**AC-2 — WHEN condition operator vocabulary**
**Given** the WHEN condition builder in the CaseColumnEditor
**When** I open the operator selector
**Then** it offers the same 13 operators as Filter Conditions (Story 10.6): `=`, `!=`, `<`, `<=`, `>`, `>=`, `IS NULL`, `IS NOT NULL`, `LIKE`, `ILIKE`, `IN`, `NOT IN`, `BETWEEN` (per FR-67 J-3 AC-2).
**Scope note:** The value input is hidden for `IS NULL` / `IS NOT NULL` (no RHS needed).

**AC-3 — THEN and ELSE values accept string literals, numbers, or column names**
**Given** the THEN or ELSE value inputs
**When** I type a value
**Then** I can enter a string literal, a numeric value, or a column name from any table on the canvas (per FR-67 J-3 AC-3).
**Scope note:** Values are stored as free-form strings. Story 11.1's `DatasetSqlGenerator.cs` interprets them (quoting strings, casting numbers, resolving column names). A dedicated column-reference picker UI (node + column dropdowns for THEN/ELSE) is deferred.

**AC-4 — Empty alias blocks Save and shows a validation banner**
**Given** a CASE column with an empty alias
**When** the canvas renders
**Then** a validation banner appears: "A custom alias is required for every CASE column."
**And** the `getCaseColumnAliasError(caseColumns)` SSOT gate returns the i18n key so Stories 11.2/11.3 can disable the Save/Preview buttons (per FR-67 J-3 AC-4).
**Scope note:** Save/Preview button-disabling is deferred to Story 11.2 (no Save button yet). The banner is wired now. Alias format also validated inline with FR-57 rules (same as column alias validation in Story 10.2).

**AC-5 — CASE column state persists to builder_state**
**Given** I configure a CASE column (alias, WHEN arms, ELSE)
**When** the `onChange` handler fires (on every change)
**Then** all data is stored in `builder_state.caseColumns[]` via `notify()` so Story 11.1's SQL generator can read `CASE WHEN … THEN … ELSE … END AS "alias"` (per FR-67 J-3 AC-5).
**Scope note:** `builder_state` is not persisted to the server until Story 11.2. This story keeps the in-memory `caseColumns` array correct via `notify()`.

**AC-6 — Expression security validator (deferred to Story 11.1)**
**Given** a valid CASE definition
**When** the builder state is saved (Story 11.2)
**Then** `ExpressionSecurityValidator.cs` (built in Story 11.1) applies the three-layer check (keyword scan + wrap-parse + final SELECT-only) before including CASE expressions in generated SQL; failure → HTTP 422 identifying the offending CASE column by alias (per AR-64 / FR-67 J-3).
**Scope note:** `ExpressionSecurityValidator.cs` and `DatasetSqlGenerator.cs` do not exist yet. This story only stores builder state correctly. The security validator is Story 11.1 scope.

---

## Tasks / Subtasks

### Task 1 — Frontend: Expand types in `builderState.ts` (AC-2, AC-5)

Open `web/src/features/datasets/types/builderState.ts`.

**Step 1 — Add `CaseOperator` type and `CASE_OPERATORS` constant.** Insert after line 9 (`export type AggregateFunction = ...`):

```typescript
// Operator vocabulary for CASE WHEN conditions — same set as Filter Conditions (Story 10.6
// reuses this type). Defined here first (Story 10.3); Story 10.6 confirms the canonical form.
export type CaseOperator =
  | '=' | '!=' | '<' | '<=' | '>' | '>='
  | 'IS NULL' | 'IS NOT NULL' | 'LIKE' | 'ILIKE'
  | 'IN' | 'NOT IN' | 'BETWEEN'

export const CASE_OPERATORS: CaseOperator[] = [
  '=', '!=', '<', '<=', '>', '>=',
  'IS NULL', 'IS NOT NULL', 'LIKE', 'ILIKE', 'IN', 'NOT IN', 'BETWEEN',
]

// One WHEN arm: column condition + THEN value.
// operandValue: raw string RHS; ignored for IS NULL / IS NOT NULL.
// thenValue: raw string; Story 11.1's SQL generator decides quoting/casting.
// Column references (AC-3) can be typed by name; Story 11.1 resolves them.
export interface CaseWhen {
  nodeId: string         // table node whose column is tested in the WHEN condition
  columnName: string     // column from that node
  operator: CaseOperator
  operandValue: string   // RHS value; empty for IS NULL / IS NOT NULL
  thenValue: string      // THEN output value
}
```

**Step 2 — Replace the `CaseColumn` stub** (the current 4-line `export interface CaseColumn { alias: string; // Epic 10 Story 10.3... }`) with:

```typescript
// Story 10.3: expanded from the Epic 10 stub.
// id: client-side stable identifier for React keys + update/delete targeting.
// nodeId: table node this CASE column is attached to (display grouping; WHEN arms
//   may reference columns from any node on the canvas per AC-3).
// alias: required (non-empty) — getCaseColumnAliasError gates Save until set (AC-4).
// elseValue: '' → no ELSE clause; any string → ELSE <elseValue> in SQL (Story 11.1).
export interface CaseColumn {
  id: string
  nodeId: string
  alias: string
  whens: CaseWhen[]    // ≥1 arms; UI ensures minimum 1 on creation
  elseValue: string    // empty string = no ELSE
}
```

**Step 3 — Add `getCaseColumnAliasError` after `isValidAlias` (end of file):**

```typescript
// Story 10.3 AC-4: gate Save/Preview when any CASE column has an empty alias.
// Returns the i18n key when invalid, null when valid.
// SSOT — consumed by the canvas validation banner (Story 10.3) and by Stories
// 11.2/11.3 to disable Save/Preview buttons.
export function getCaseColumnAliasError(
  caseColumns: CaseColumn[],
): 'datasets.builder.validation.caseColumnRequiresAlias' | null {
  return caseColumns.some((c) => c.alias === '')
    ? 'datasets.builder.validation.caseColumnRequiresAlias'
    : null
}
```

**No changes to `BuilderState`, `EMPTY_BUILDER_STATE`, or `FilterCondition`** — `BuilderState.caseColumns: CaseColumn[]` and the empty array init are already correct. `FilterCondition` stays as its stub (Story 10.6 expands it).

---

### Task 2 — Frontend: Create `CaseColumnEditor.tsx` (AC-1, AC-2, AC-3, AC-4)

Create `web/src/components/query-builder/CaseColumnEditor.tsx`:

```typescript
import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  CASE_OPERATORS,
  type CaseColumn,
  type CaseOperator,
  type CaseWhen,
} from '../../features/datasets/types/builderState'
import type { TableNodeType } from './TableNode'

interface CaseColumnEditorProps {
  caseColumn: CaseColumn
  nodes: TableNodeType[]
  onUpdate: (updated: CaseColumn) => void
  onClose: () => void
}

const OPERATORS_WITHOUT_VALUE: CaseOperator[] = ['IS NULL', 'IS NOT NULL']

export function CaseColumnEditor({ caseColumn, nodes, onUpdate, onClose }: CaseColumnEditorProps) {
  const { t } = useTranslation()
  const aliasError = caseColumn.alias !== '' && !isValidAlias(caseColumn.alias)
  const aliasErrorId = `case-${caseColumn.id}-alias-error`

  function updateWhen(index: number, patch: Partial<CaseWhen>) {
    onUpdate({
      ...caseColumn,
      whens: caseColumn.whens.map((w, i) => (i === index ? { ...w, ...patch } : w)),
    })
  }

  function addWhen() {
    const defaultNodeId = nodes[0]?.id ?? caseColumn.nodeId
    const defaultColumn = nodes.find((n) => n.id === defaultNodeId)?.data.columns[0]?.columnName ?? ''
    const newWhen: CaseWhen = {
      nodeId: defaultNodeId,
      columnName: defaultColumn,
      operator: '=',
      operandValue: '',
      thenValue: '',
    }
    onUpdate({ ...caseColumn, whens: [...caseColumn.whens, newWhen] })
  }

  function deleteWhen(index: number) {
    onUpdate({ ...caseColumn, whens: caseColumn.whens.filter((_, i) => i !== index) })
  }

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-80 overflow-y-auto rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      style={{ maxHeight: 'calc(100% - 2rem)' }}
      role="complementary"
      aria-label={t('datasets.builder.caseEditor.title')}
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">{t('datasets.builder.caseEditor.title')}</span>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('datasets.builder.caseEditor.closeAria')}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Alias */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.caseEditor.aliasLabel')}
          </p>
          <input
            type="text"
            value={caseColumn.alias}
            onChange={(e) => onUpdate({ ...caseColumn, alias: e.target.value })}
            aria-label={t('datasets.builder.caseEditor.aliasLabel')}
            aria-invalid={aliasError || caseColumn.alias === ''}
            aria-describedby={aliasError ? aliasErrorId : undefined}
            placeholder="e.g. price_category"
            className={`h-7 w-full rounded border bg-background px-2 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
              aliasError ? 'border-destructive' : 'border-border'
            }`}
          />
          {caseColumn.alias === '' && (
            <p className="text-xs text-destructive">
              {t('datasets.builder.caseEditor.aliasRequired')}
            </p>
          )}
          {aliasError && (
            <p id={aliasErrorId} className="text-xs text-destructive">
              {t('datasets.builder.node.aliasInvalidPattern')}
            </p>
          )}
        </div>

        {/* WHEN arms */}
        <div className="space-y-2">
          {caseColumn.whens.map((when, index) => {
            const nodeColumns =
              nodes.find((n) => n.id === when.nodeId)?.data.columns ?? []
            const hasValue = !OPERATORS_WITHOUT_VALUE.includes(when.operator)
            return (
              <div key={index} className="space-y-1.5 rounded-md border border-border p-2">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-medium text-muted-foreground">
                    {t('datasets.builder.caseEditor.whenSection')} {index + 1}
                  </span>
                  {caseColumn.whens.length > 1 && (
                    <button
                      type="button"
                      onClick={() => deleteWhen(index)}
                      aria-label={t('datasets.builder.caseEditor.deleteWhenAria')}
                      className="flex h-4 w-4 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    >
                      <X className="h-3 w-3" />
                    </button>
                  )}
                </div>

                {/* Table (node) selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.tableLabel')}
                  </p>
                  <select
                    value={when.nodeId}
                    onChange={(e) => {
                      const newNodeId = e.target.value
                      const firstCol =
                        nodes.find((n) => n.id === newNodeId)?.data.columns[0]?.columnName ?? ''
                      updateWhen(index, { nodeId: newNodeId, columnName: firstCol })
                    }}
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {nodes.map((n) => (
                      <option key={n.id} value={n.id}>
                        {n.data.tableName}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Column selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.columnLabel')}
                  </p>
                  <select
                    value={when.columnName}
                    onChange={(e) => updateWhen(index, { columnName: e.target.value })}
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {nodeColumns.map((c) => (
                      <option key={c.columnName} value={c.columnName}>
                        {c.columnName}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Operator selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.operatorLabel')}
                  </p>
                  <select
                    value={when.operator}
                    onChange={(e) =>
                      updateWhen(index, {
                        operator: e.target.value as CaseOperator,
                        operandValue: '',
                      })
                    }
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {CASE_OPERATORS.map((op) => (
                      <option key={op} value={op}>
                        {op}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Operand value (hidden for IS NULL / IS NOT NULL) */}
                {hasValue && (
                  <div className="space-y-0.5">
                    <p className="text-xs text-muted-foreground">
                      {t('datasets.builder.caseEditor.valueLabel')}
                    </p>
                    <input
                      type="text"
                      value={when.operandValue}
                      onChange={(e) => updateWhen(index, { operandValue: e.target.value })}
                      className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    />
                  </div>
                )}

                {/* THEN value */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.thenLabel')}
                  </p>
                  <input
                    type="text"
                    value={when.thenValue}
                    onChange={(e) => updateWhen(index, { thenValue: e.target.value })}
                    placeholder={t('datasets.builder.caseEditor.thenValuePlaceholder')}
                    className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  />
                </div>
              </div>
            )
          })}
        </div>

        {/* Add WHEN */}
        <button
          type="button"
          onClick={addWhen}
          className="w-full rounded border border-dashed border-border py-1 text-xs text-muted-foreground hover:bg-overlay-hover focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          {t('datasets.builder.caseEditor.addWhenButton')}
        </button>

        {/* ELSE */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.caseEditor.elseSection')}
          </p>
          <input
            type="text"
            value={caseColumn.elseValue}
            onChange={(e) => onUpdate({ ...caseColumn, elseValue: e.target.value })}
            placeholder={t('datasets.builder.caseEditor.elseValuePlaceholder')}
            className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
        </div>
      </div>
    </div>
  )
}
```

**Critical notes:**
- `onUpdate` is called on every input change (live editing, no local save state) — same pattern as `JoinInspector` calling `onJoinTypeChange` immediately on select.
- `OPERATORS_WITHOUT_VALUE` hides the operand input for `IS NULL` / `IS NOT NULL` (AC-2).
- When the table (nodeId) changes, `columnName` resets to the first column of the new table to avoid stale references.
- All alias validation reuses `isValidAlias` from `builderState.ts` (same FR-57 rules as Story 10.2). The existing `datasets.builder.node.aliasInvalidPattern` key is reused — no new key needed for the pattern message.
- `maxHeight: 'calc(100% - 2rem)'` + `overflow-y-auto` prevents the panel from overflowing the canvas on small viewports or with many WHEN arms.
- The panel is `z-50` (same as `JoinInspector`) and `nodrag nopan`.

---

### Task 3 — Frontend: Add 4 new contexts + CASE column section to `TableNode.tsx` (AC-1, AC-4, AC-5)

Open `web/src/components/query-builder/TableNode.tsx`.

**Step 1 — Update imports.** Add `X` from lucide-react and `CaseColumn` from builderState:

```typescript
import { X } from 'lucide-react'
import { memo, createContext, useContext } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  type AggregateFunction,
  type CaseColumn,
  type TableNodeData,
  type TableSide,
} from '../../features/datasets/types/builderState'
```

**Step 2 — Export 4 new contexts** directly after `ColumnAliasContext` (after line 36):

```typescript
// Story 10.3: canvas provides the full caseColumns array so each TableNode
// can filter for its own (by nodeId) and render compact CASE column rows.
// Value changes when caseColumns state changes — all nodes re-render then,
// which is acceptable for small canvases.
export const CaseColumnsContext = createContext<CaseColumn[]>([])

// Story 10.3: canvas provides add-case handler. Stable-ref via useCallback
// with deps [notify] — does not change on every render.
export const AddCaseColumnContext = createContext<(nodeId: string) => void>(() => {})

// Story 10.3: canvas provides delete-case handler. Same stable-ref posture.
export const DeleteCaseColumnContext = createContext<(caseId: string) => void>(() => {})

// Story 10.3: canvas provides select-case handler (opens CaseColumnEditor).
// Stable-ref: deps [].
export const SelectCaseColumnContext = createContext<(caseId: string) => void>(() => {})
```

**Step 3 — Add context reads inside `TableNode`.** After `const onColumnAlias = useContext(ColumnAliasContext)` (line 47), add:

```typescript
const caseColumns = useContext(CaseColumnsContext)
const onAddCaseColumn = useContext(AddCaseColumnContext)
const onDeleteCaseColumn = useContext(DeleteCaseColumnContext)
const onSelectCaseColumn = useContext(SelectCaseColumnContext)
// Filter to this node's CASE columns only
const nodeCaseColumns = caseColumns.filter((c) => c.nodeId === id)
```

**Step 4 — Add CASE columns section and "Add Case" button** at the bottom of the node JSX, after the closing `</div>` of the column list (`{/* Column list ... */}`) and before the outer node `</div>`:

```tsx
{/* CASE column rows — Story 10.3: one compact row per CASE column on this node */}
{nodeCaseColumns.map((cc) => (
  <div key={cc.id} className="flex items-center gap-2 border-t border-border px-3 py-1.5">
    <button
      type="button"
      onClick={() => onSelectCaseColumn(cc.id)}
      className="nodrag nopan min-w-0 flex-1 truncate rounded text-left text-xs text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
      aria-label={cc.alias || t('datasets.builder.node.caseColumnUntitled')}
    >
      <span className="font-mono">
        {cc.alias || t('datasets.builder.node.caseColumnUntitled')}
      </span>
    </button>
    <button
      type="button"
      onClick={() => onDeleteCaseColumn(cc.id)}
      aria-label={t('datasets.builder.node.deleteCaseColumnAria')}
      className="nodrag nopan flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
    >
      <X className="h-3 w-3" />
    </button>
  </div>
))}
{/* Add Case button — always visible; triggers add + auto-opens editor */}
<div className="border-t border-border px-3 py-1.5">
  <button
    type="button"
    onClick={() => onAddCaseColumn(id)}
    className="nodrag nopan w-full rounded text-left text-xs text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
  >
    {t('datasets.builder.node.addCaseButton')}
  </button>
</div>
```

**Step 5 — Update the component-level comment** (after the Story 10.2 mention):

```typescript
// Story 9.1: Structural shell. Story 9.5 makes the Left/Right toggle interactive.
// Story 10.1 makes column checkboxes interactive via ColumnCheckContext.
// Story 10.2 adds aggregate dropdown + alias input for checked columns.
// Story 10.3 adds CASE column rows + "Add Case" button (CaseColumnsContext,
//   AddCaseColumnContext, DeleteCaseColumnContext, SelectCaseColumnContext).
```

---

### Task 4 — Frontend: Wire state, handlers, providers, and editor in `QueryBuilderCanvas.tsx` (AC-1, AC-4, AC-5)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

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
  type TableNodeType,
} from '../../components/query-builder/TableNode'
```

**Step 2 — Add `CaseColumnEditor` import:**

```typescript
import { CaseColumnEditor } from '../../components/query-builder/CaseColumnEditor'
```

**Step 3 — Update the `builderState` import.** Add new types:

```typescript
import {
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  getCaseColumnAliasError,
  type AggregateFunction,
  type BuilderState,
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

**Step 4 — Add `caseColumns` state + ref** inside `QueryBuilderCanvasInner`, after the `extraStateRef` block (~line 72):

```typescript
// Story 10.3: caseColumns state drives the TableNode CASE column rows and the
// CaseColumnEditor panel. caseColumnsRef lets handlers read the latest array
// without closing over stale state (same posture as nodesRef/edgesRef).
const [caseColumns, setCaseColumns] = useState<CaseColumn[]>(initialState.caseColumns)
const caseColumnsRef = useRef(caseColumns)
caseColumnsRef.current = caseColumns

// Story 10.3: tracks which CASE column is open in CaseColumnEditor (null = closed).
// Mutually exclusive with selectedEdge (closing one does not close the other, but
// opening one closes the other — see onPaneClick and the handler for each).
const [selectedCaseColumnId, setSelectedCaseColumnId] = useState<string | null>(null)
```

**Step 5 — Update `onPaneClick`** to also close the CASE column editor:

```typescript
const onPaneClick = useCallback(() => {
  setSelectedEdge(null)
  setSelectedCaseColumnId(null)
}, [])
```

**Step 6 — Add `handleAddCaseColumn`** after `handleAliasChange` (~line 274):

```typescript
// Story 10.3 AC-1/AC-5: create a new CASE column for the given node, auto-open
// the editor, and emit via notify(). Reads refs (not closure state) for stable-ref
// posture so AddCaseColumnContext.Provider value is referentially stable.
const handleAddCaseColumn = useCallback(
  (nodeId: string) => {
    const newId = `case-${nodeId}-${Date.now()}`
    const firstColumn =
      nodesRef.current.find((n) => n.id === nodeId)?.data.columns[0]?.columnName ?? ''
    const defaultWhen: CaseWhen = {
      nodeId,
      columnName: firstColumn,
      operator: '=',
      operandValue: '',
      thenValue: '',
    }
    const newCase: CaseColumn = {
      id: newId,
      nodeId,
      alias: '',
      whens: [defaultWhen],
      elseValue: '',
    }
    const updated = [...caseColumnsRef.current, newCase]
    setCaseColumns(updated)
    extraStateRef.current = { ...extraStateRef.current, caseColumns: updated }
    notify(nodesRef.current, edgesRef.current)
    setSelectedCaseColumnId(newId)
    setSelectedEdge(null)  // close JoinInspector if open (mutual exclusivity)
  },
  [notify],
)

// Story 10.3 AC-5: delete a CASE column by id and emit via notify().
const handleDeleteCaseColumn = useCallback(
  (caseId: string) => {
    const updated = caseColumnsRef.current.filter((c) => c.id !== caseId)
    setCaseColumns(updated)
    extraStateRef.current = { ...extraStateRef.current, caseColumns: updated }
    notify(nodesRef.current, edgesRef.current)
    setSelectedCaseColumnId((prev) => (prev === caseId ? null : prev))
  },
  [notify],
)

// Story 10.3: open the CaseColumnEditor for the given case id.
// Deps []: setSelectedCaseColumnId and setSelectedEdge are stable setters.
const handleSelectCaseColumn = useCallback((caseId: string) => {
  setSelectedCaseColumnId(caseId)
  setSelectedEdge(null)  // close JoinInspector (mutual exclusivity)
}, [])

// Story 10.3 AC-5: update a CASE column in-place and emit via notify().
// Called by CaseColumnEditor on every field change (live editing).
const handleUpdateCaseColumn = useCallback(
  (updated: CaseColumn) => {
    const updatedAll = caseColumnsRef.current.map((c) => (c.id === updated.id ? updated : c))
    setCaseColumns(updatedAll)
    extraStateRef.current = { ...extraStateRef.current, caseColumns: updatedAll }
    notify(nodesRef.current, edgesRef.current)
  },
  [notify],
)
```

**Step 7 — Update `handleNodesChange`** to cascade CASE column deletion when a node is removed. Inside the `if (removedIds.size > 0)` block, **before** the `notify(survivingNodes, survivingEdges)` call, add:

```typescript
// Story 10.3: cascade CASE column removal when their associated node is deleted.
// extraStateRef MUST be updated before notify() so the parent receives consistent state.
const survivingCaseColumns = caseColumnsRef.current.filter(
  (c) => !removedIds.has(c.nodeId),
)
if (survivingCaseColumns.length !== caseColumnsRef.current.length) {
  setCaseColumns(survivingCaseColumns)
  extraStateRef.current = { ...extraStateRef.current, caseColumns: survivingCaseColumns }
  setSelectedCaseColumnId((prev) => {
    if (!prev) return null
    const target = caseColumnsRef.current.find((c) => c.id === prev)
    return target && removedIds.has(target.nodeId) ? null : prev
  })
}
```

The existing `notify(survivingNodes, survivingEdges)` call follows immediately after — it will now pick up `extraStateRef.current.caseColumns` with the cascaded removal.

**Step 8 — Add `caseColumnAliasError` to the validation computation** (after `columnSelectionError` derivation, ~line 406):

```typescript
// Story 10.3 AC-4: gate Save/Preview when any CASE column has an empty alias.
const caseColumnAliasError = getCaseColumnAliasError(caseColumns)
```

**Step 9 — Extend the provider stack** around `<ReactFlow>`:

```tsx
<TableSideChangeContext.Provider value={handleSideChange}>
  <ColumnCheckContext.Provider value={handleColumnCheck}>
    <ColumnAggregateContext.Provider value={handleAggregateChange}>
      <ColumnAliasContext.Provider value={handleAliasChange}>
        <CaseColumnsContext.Provider value={caseColumns}>
          <AddCaseColumnContext.Provider value={handleAddCaseColumn}>
            <DeleteCaseColumnContext.Provider value={handleDeleteCaseColumn}>
              <SelectCaseColumnContext.Provider value={handleSelectCaseColumn}>
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
              </SelectCaseColumnContext.Provider>
            </DeleteCaseColumnContext.Provider>
          </AddCaseColumnContext.Provider>
        </CaseColumnsContext.Provider>
      </ColumnAliasContext.Provider>
    </ColumnAggregateContext.Provider>
  </ColumnCheckContext.Provider>
</TableSideChangeContext.Provider>
```

**Step 10 — Update validation banner** to include the CASE column alias error:

```tsx
{(leftTableError || columnSelectionError || caseColumnAliasError) && (
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
    {caseColumnAliasError && (
      <div
        role="alert"
        className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
      >
        {t('datasets.builder.validation.caseColumnRequiresAlias')}
      </div>
    )}
  </div>
)}
```

**Step 11 — Add derived `selectedCaseColumn` and render `CaseColumnEditor`** after the JoinInspector render:

```typescript
// Derive the full CaseColumn object for the editor (null if id is stale after deletion)
const selectedCaseColumn =
  selectedCaseColumnId !== null
    ? (caseColumns.find((c) => c.id === selectedCaseColumnId) ?? null)
    : null
```

```tsx
{selectedCaseColumn && (
  <CaseColumnEditor
    caseColumn={selectedCaseColumn}
    nodes={nodes}
    onUpdate={handleUpdateCaseColumn}
    onClose={() => setSelectedCaseColumnId(null)}
  />
)}
```

**Step 12 — Update `onEdgeClick`** to close the CASE column editor for mutual exclusivity:

```typescript
const onEdgeClick = useCallback(
  (_event: React.MouseEvent, edge: JoinEdgeType) => {
    setSelectedEdge(edge)
    setSelectedCaseColumnId(null)  // close CASE editor (mutual exclusivity)
  },
  [],
)
```

---

### Task 5 — Frontend: Add i18n keys to `en.json` (AC-1, AC-2, AC-4)

Open `web/src/lib/i18n/locales/en.json`.

**Step 1 — Extend `datasets.builder.node`** (after `aliasInvalidPattern`):

```json
"node": {
  "left": "Left",
  "right": "Right",
  "sideGroupAria": "Table side designation",
  "aliasInvalidPattern": "Use lowercase letters, digits, and underscores; must start with a letter or underscore (max 63 chars).",
  "addCaseButton": "+ Add Case",
  "caseColumnUntitled": "(untitled)",
  "deleteCaseColumnAria": "Delete case column"
}
```

**Step 2 — Extend `datasets.builder.validation`** (after `noColumnsSelected`):

```json
"validation": {
  "noLeftTable": "Designate one table as the left (FROM) table.",
  "noColumnsSelected": "Select at least one column.",
  "caseColumnRequiresAlias": "A custom alias is required for every CASE column."
}
```

**Step 3 — Add `datasets.builder.caseEditor` section** (after `datasets.builder.validation`):

```json
"caseEditor": {
  "title": "CASE Column",
  "closeAria": "Close case editor",
  "aliasLabel": "Alias",
  "aliasRequired": "Alias is required.",
  "whenSection": "WHEN",
  "thenLabel": "THEN",
  "elseSection": "ELSE (optional)",
  "elseValuePlaceholder": "Value or leave blank for no ELSE",
  "addWhenButton": "+ Add WHEN",
  "deleteWhenAria": "Remove WHEN arm",
  "tableLabel": "Table",
  "columnLabel": "Column",
  "operatorLabel": "Operator",
  "valueLabel": "Value",
  "thenValuePlaceholder": "THEN value"
}
```

Total new keys: 17. All are referenced by static `t('…')` literals in `TableNode.tsx`, `QueryBuilderCanvas.tsx`, and `CaseColumnEditor.tsx` → **i18n-check must exit 0**.

Do **not** add a new key for alias pattern validation in the CASE editor — it reuses the existing `datasets.builder.node.aliasInvalidPattern` (same FR-57 rule).

---

### Task 6 — Frontend: Unit tests for `getCaseColumnAliasError` (AC-4)

Open `web/src/features/datasets/types/__tests__/builderState.test.ts`.

**Step 1 — Update the import** to add `getCaseColumnAliasError` and `CaseColumn`:

```typescript
import {
  getColumnSelectionValidationError,
  getLeftTableValidationError,
  getCaseColumnAliasError,
  isValidAlias,
} from '../builderState'
import type { CaseColumn, ColumnSelection, TableNodeState, TableSide } from '../builderState'
```

**Step 2 — Add a helper** after `nodeWithColumns`:

```typescript
function caseCol(alias: string): CaseColumn {
  return {
    id: 'c1',
    nodeId: 'n1',
    alias,
    whens: [{ nodeId: 'n1', columnName: 'id', operator: '=', operandValue: '1', thenValue: 'a' }],
    elseValue: '',
  }
}
```

**Step 3 — Add `describe('getCaseColumnAliasError', ...)` block** after the `isValidAlias` describe:

```typescript
describe('getCaseColumnAliasError', () => {
  it('returns null for an empty caseColumns array (nothing to validate)', () => {
    expect(getCaseColumnAliasError([])).toBeNull()
  })

  it('returns null when all case columns have non-empty aliases', () => {
    expect(getCaseColumnAliasError([caseCol('price_tier'), caseCol('status_label')])).toBeNull()
  })

  it('returns the i18n key when one case column has an empty alias', () => {
    expect(getCaseColumnAliasError([caseCol('price_tier'), caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns the i18n key when all case columns have empty aliases', () => {
    expect(getCaseColumnAliasError([caseCol(''), caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns the i18n key for a single case column with an empty alias', () => {
    expect(getCaseColumnAliasError([caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns null for a single case column with a valid alias', () => {
    expect(getCaseColumnAliasError([caseCol('my_case')])).toBeNull()
  })
})
```

Expected: **310 passed / 0 failed** (304 baseline + 6 new).

---

### Task 7 — Frontend: Add deferred-work entry

Add to `_bmad-output/implementation-artifacts/deferred-work.md` at the top (after the file heading):

```markdown
## Deferred from: implementation of 10-3-add-case-column

- **AC-3 column-reference picker for THEN/ELSE deferred** — THEN/ELSE values are free-form text inputs in Story 10.3. The user can enter column names; Story 11.1's `DatasetSqlGenerator.cs` determines quoting/casting. A dedicated UX (node + column dropdowns for THEN/ELSE) can be added when Story 11.1 defines the column-ref encoding needed by the SQL generator. [`CaseColumnEditor.tsx`, `builderState.ts`]
- **Save/Preview blocking on empty CASE column alias (AC-4) deferred to Story 11.2** — `getCaseColumnAliasError(caseColumns)` is the SSOT gate; 11.2 must call it alongside `getLeftTableValidationError` / `getColumnSelectionValidationError` and set `disabled` on the Save button. The banner is wired now. [`builderState.ts`, `QueryBuilderCanvas.tsx`]
- **ExpressionSecurityValidator.cs and DatasetSqlGenerator.cs (AC-6) deferred to Story 11.1** — neither file exists yet. Story 11.1 implements both the SQL generator and the three-layer expression security check (keyword scan + wrap-parse + final SELECT-only). [`DatasetSqlGenerator.cs`, `ExpressionSecurityValidator.cs`]
- **BuilderStateDto.cs CaseColumn shape deferred to Story 11.1** — the C# DTO mirror does not exist yet; must be authored in coordination with the SQL generator. The TypeScript `CaseColumn` interface (id, nodeId, alias, whens, elseValue) is the cross-layer contract; any deviation in C# is a breaking change per Decision 6.11. [`DatasetSqlGenerator.cs`]
- **IN / NOT IN / BETWEEN operator UX deferred** — Story 10.3 renders a single text input for all value-bearing operators including IN, NOT IN, and BETWEEN. Story 10.6 (Filter Conditions) will define the canonical multi-value / two-value input UX; align the CASE editor's operator value inputs with that story's approach. [`CaseColumnEditor.tsx`]
```

---

### Task 8 — Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → 310 passed / 0 failed (304 baseline + 6 new `getCaseColumnAliasError` tests)
- [x] `cd web && node scripts/i18n-check.mjs` → exit 0 (all 17 new keys referenced by static `t()` literals; 89 pre-existing orphaned warnings unchanged)
- [x] Manual smoke (AC-1, Add Case creates row): verified by code inspection — `handleAddCaseColumn` creates `CaseColumn` with `id`/`nodeId`/`alias:''`/`whens:[defaultWhen]`/`elseValue:''`, appends to `caseColumnsRef.current`, calls `setCaseColumns`, updates `extraStateRef`, calls `notify`, sets `selectedCaseColumnId`. `TableNode` renders the row because `nodeCaseColumns.filter((c) => c.nodeId === id)` includes the new column.
- [x] Manual smoke (AC-2, operator vocabulary): verified — `CASE_OPERATORS` exports all 13 operators; `<select>` in editor maps each to an `<option>`; IS NULL / IS NOT NULL hide the operand input via `OPERATORS_WITHOUT_VALUE`.
- [x] Manual smoke (AC-4, alias required banner): verified — `getCaseColumnAliasError([{ alias: '' }])` returns the i18n key; banner condition `caseColumnAliasError` is truthy → banner renders.
- [x] Manual smoke (AC-5, state persists): verified — `handleUpdateCaseColumn` writes to `caseColumnsRef.current`, updates `extraStateRef.current.caseColumns`, calls `notify` → parent `onChange` receives the full `BuilderState` including updated `caseColumns`.
- [x] Manual smoke (cascade node delete): verified — `handleNodesChange` computes `survivingCaseColumns` before `notify(survivingNodes, survivingEdges)`; `extraStateRef.current.caseColumns` is updated before the notify call so the parent receives consistent state.
- [x] Manual smoke (mutual exclusivity): verified — `handleSelectCaseColumn` calls `setSelectedEdge(null)`; `onEdgeClick` calls `setSelectedCaseColumnId(null)`; `onPaneClick` calls both setters to null.
- [x] Manual smoke (nodrag/nopan): verified — all interactive elements in `TableNode` CASE rows and `CaseColumnEditor` carry `nodrag nopan` or are inside an `nodrag nopan` container.
- [x] Backend baseline: no backend work in this story (DatasetSqlGenerator.cs, ExpressionSecurityValidator.cs deferred to Story 11.1).

### Review Findings

- [x] [Review][Patch] getCaseColumnAliasError accepts whitespace-only aliases — gate checks `alias === ''` but `'   '` is also effectively empty and passes; fix: `c.alias.trim() === ''` [`web/src/features/datasets/types/builderState.ts`]
- [x] [Review][Patch] Date.now() ID collision under rapid successive clicks — two Add Case clicks within 1ms on the same node produce identical IDs, corrupting update/delete targeting; fix: use a stable counter ref or `crypto.randomUUID()` [`web/src/features/datasets/QueryBuilderCanvas.tsx`]
- [x] [Review][Patch] addWhen() defaults new WHEN arm to nodes[0] instead of caseColumn.nodeId — if nodes[0] is a different table, the new arm silently references the wrong table; fix: always default to `caseColumn.nodeId` [`web/src/components/query-builder/CaseColumnEditor.tsx`]
- [x] [Review][Patch] aria-describedby not linked for empty-alias error in CaseColumnEditor — `aria-invalid` is set but the required-error `<p>` has no `id`, so screen readers announce invalid without description; fix: add `id` to the empty-alias `<p>` and include it alongside `aliasErrorId` in `aria-describedby` [`web/src/components/query-builder/CaseColumnEditor.tsx`]
- [x] [Review][Defer] deleteWhen has no function-level minimum WHEN-arm guard [`web/src/components/query-builder/CaseColumnEditor.tsx`] — deferred, pre-existing; UI-only guard is spec intent; function is component-private
- [x] [Review][Defer] IN/NOT IN/BETWEEN use single text input for operand [`web/src/components/query-builder/CaseColumnEditor.tsx`] — deferred, already in deferred-work.md (10-3 impl entry); Story 10.6/11.1 scope
- [x] [Review][Defer] Empty-columns node causes empty column select in WHEN arm with no user feedback [`web/src/components/query-builder/CaseColumnEditor.tsx`] — deferred, pre-existing edge case across canvas; canvas assumes tables have columns

---

## Dev Notes

### §1 — Current State Left by Story 10.2

`builderState.ts`: `CaseColumn` is a 4-line stub `{ alias: string; // Epic 10 Story 10.3... }`. `FilterCondition` is also a stub. `BuilderState.caseColumns: CaseColumn[]` and `EMPTY_BUILDER_STATE` (`caseColumns: []`) are already correct — no changes needed to those.

`TableNode.tsx` (lines 1–169): no CASE column UI exists. The node renders header + column list only. Imports do not include `X` from lucide-react.

`QueryBuilderCanvas.tsx` (lines 1–478): `extraStateRef.current.caseColumns` is initialized from `initialState.caseColumns` at mount and **never updated** (no CASE column handlers exist). This story adds `caseColumns` React state + ref + 4 handlers + cascade deletion, closing the gap noted in deferred-work W1 ("when Epic 10 makes those fields editable").

`CaseColumnEditor.tsx`: does not exist — this story creates it.

### §2 — Why `caseColumns` is React State (not just `extraStateRef`)

The `extraStateRef` pattern keeps `notify()` current without causing re-renders. But `CaseColumn` rows in `TableNode` and the `CaseColumnEditor` panel are UI — they MUST trigger re-renders when CASE columns change. So `caseColumns` is `useState` (drives UI) + `caseColumnsRef` (for stable callback reads) + `extraStateRef.current.caseColumns` (kept in sync by handlers before `notify()`). This is identical to how `nodes` (`useNodesState`) + `nodesRef` + `notify` interact.

### §3 — Stable-Ref Pattern for CASE Column Handlers

All four handlers (`handleAddCaseColumn`, `handleDeleteCaseColumn`, `handleSelectCaseColumn`, `handleUpdateCaseColumn`) read from refs, not closure state. `AddCaseColumnContext.Provider value`, `DeleteCaseColumnContext.Provider value`, and `SelectCaseColumnContext.Provider value` stay referentially stable so `TableNode`'s `memo()` is not defeated for context changes not related to the providers' values.

`CaseColumnsContext.Provider value={caseColumns}` — this IS state, so it changes whenever any CASE column is added/deleted/updated. All `TableNode` instances re-render on such changes, bypassing `memo()`. Acceptable for small canvases; if performance is a concern, a `useMemo`-stabilized selector per nodeId could be introduced.

### §4 — `extraStateRef` Update Ordering in `handleNodesChange`

`extraStateRef.current.caseColumns` MUST be updated **before** calling `notify(survivingNodes, survivingEdges)` in `handleNodesChange`. The `notify` function spreads `extraStateRef.current`; if CASE column removal happens after `notify`, the parent receives stale (pre-deletion) CASE columns for one cycle. The existing `setEdges(survivingEdges)` + `notify(survivingNodes, survivingEdges)` pattern has the same ordering requirement.

### §5 — Mutual Exclusivity of `JoinInspector` and `CaseColumnEditor`

Both panels are positioned `absolute right-4 top-4 z-50`. Rendering both simultaneously causes visual overlap. Story 10.3 enforces mutual exclusivity:
- `handleSelectCaseColumn` → `setSelectedEdge(null)`
- `handleAddCaseColumn` → `setSelectedEdge(null)`
- `onEdgeClick` → `setSelectedCaseColumnId(null)`
- `onPaneClick` → both set to null

Story 10.4 (Calculated Column) will add a third editor. At that point, consider a `selectedPanel: { type: 'join' | 'case' | 'calculated'; id: string } | null` unified selector to avoid N mutual-exclusivity pairs.

### §6 — `FilterCondition` Stub Unchanged

`FilterCondition` in `builderState.ts` remains as the stub from Epic 8 (`tableName`, `columnName`, `operator: string`, `value: string | null`). Story 10.3 introduces a SEPARATE `CaseWhen` type for CASE conditions rather than expanding `FilterCondition`. Story 10.6 (Filter Conditions Dialog) will define the canonical full `FilterCondition`; at that point, align `CaseWhen` with it if the shapes overlap cleanly.

### §7 — No Backend / No `BuilderStateDto` Changes

- `DatasetSqlGenerator.cs` does not exist yet (Story 11.1).
- `ExpressionSecurityValidator.cs` does not exist yet (Story 11.1).
- `BuilderStateDto.cs` does not exist yet (Story 11.1).
- No API calls, no migration. Server persistence is Story 11.2.
- Backend baseline: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures). No backend work in this story.

### §8 — Files to Create / Modify

```
MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — add CaseOperator type, CASE_OPERATORS constant, CaseWhen interface
    — expand CaseColumn stub to full interface (id, nodeId, alias, whens, elseValue)
    — add getCaseColumnAliasError() SSOT gate
  web/src/features/datasets/types/__tests__/builderState.test.ts
    — add getCaseColumnAliasError import + CaseColumn type import
    — add caseCol() helper
    — add 6-case describe block for getCaseColumnAliasError
  web/src/components/query-builder/TableNode.tsx
    — add X from lucide-react import; add CaseColumn to builderState import
    — export CaseColumnsContext, AddCaseColumnContext, DeleteCaseColumnContext, SelectCaseColumnContext
    — add context reads + nodeCaseColumns filter in TableNode component
    — add CASE column rows + "Add Case" button to node JSX
    — update component comment
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — imports: 4 new contexts from TableNode, CaseColumnEditor, getCaseColumnAliasError,
      CaseColumn, CaseWhen from builderState
    — state: caseColumns (useState), caseColumnsRef (useRef), selectedCaseColumnId (useState)
    — handlers: handleAddCaseColumn, handleDeleteCaseColumn, handleSelectCaseColumn,
      handleUpdateCaseColumn (all stable via nodesRef/edgesRef/caseColumnsRef pattern)
    — onPaneClick: add setSelectedCaseColumnId(null)
    — onEdgeClick: add setSelectedCaseColumnId(null) for mutual exclusivity
    — handleNodesChange: cascade CASE column removal before notify()
    — validation: add caseColumnAliasError = getCaseColumnAliasError(caseColumns)
    — provider stack: 4 new providers wrapping <ReactFlow>
    — validation banner: add caseColumnAliasError branch
    — derived selectedCaseColumn + CaseColumnEditor render
  web/src/lib/i18n/locales/en.json
    — datasets.builder.node: 3 new keys (addCaseButton, caseColumnUntitled, deleteCaseColumnAria)
    — datasets.builder.validation: 1 new key (caseColumnRequiresAlias)
    — datasets.builder.caseEditor: 13 new keys (title, closeAria, aliasLabel, aliasRequired,
      whenSection, thenLabel, elseSection, elseValuePlaceholder, addWhenButton,
      deleteWhenAria, tableLabel, columnLabel, operatorLabel, valueLabel, thenValuePlaceholder)

NEW:
  web/src/components/query-builder/CaseColumnEditor.tsx
    — full CASE/WHEN builder panel (alias, WHEN arms with table/column/operator/value/then,
      Add WHEN, ELSE) — live editing via onUpdate prop, close via onClose

DOC:
  _bmad-output/implementation-artifacts/deferred-work.md
    — add 10-3 deferral entries (AC-3 picker, AC-4 save-blocking, AC-6 backend, DTO, IN/BETWEEN UX)
```

### §9 — Git / Recent-Work Patterns (from Stories 10.1 and 10.2)

- One context per action type, stable-ref via `useCallback` with minimal deps — follow exactly.
- `nodrag nopan` on every interactive element inside a node or floating panel.
- No hardcoded colors — use theme tokens (`bg-background`, `border-border`, `text-destructive`).
- New i18n keys must have a matching static `t('key.name')` literal call (i18n-lint fatal).
- New `describe` blocks appended to `builderState.test.ts` without modifying existing tests.
- All 4 new handlers mirror `handleColumnCheck`'s stable-ref posture (read refs, deps `[notify]` only or `[]`).

### §10 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 10.3 ACs (FR-67 J-3 AC-1..AC-5)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.8 — ExpressionSecurityValidator (Story 11.1)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.10 — SQL generator CASE column rendering]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.11 — builder_state CaseColumn contract]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.12 — "Add Case" button on TableNode]
- [Source: `web/src/features/datasets/types/builderState.ts` — CaseColumn stub, EMPTY_BUILDER_STATE]
- [Source: `web/src/components/query-builder/TableNode.tsx` — current node structure (post-10.2)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — extraStateRef, nodesRef, notify pattern]
- [Source: `web/src/components/query-builder/JoinInspector.tsx` — floating panel pattern to mirror]
- [Source: `_bmad-output/implementation-artifacts/10-2-aggregate-function-and-custom-column-alias.md §2,§3` — context rationale + stable-ref pattern]
- [Source: Memory — @xyflow/react v12 typing gotchas: data must be type aliases not interfaces (applies to TableNodeData; CaseColumn is top-level, not constrained)]
- [Source: Memory — i18n-lint failure RESOLVED; a red i18n-lint is now a real regression]
- [Source: Memory — Validators registered explicitly; note for future backend stories]

---

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 — BMad create-story workflow

### Completion Notes List

- Expanded `builderState.ts`: added `CaseOperator` union type (13 operators), `CASE_OPERATORS` constant array, `CaseWhen` interface (nodeId, columnName, operator, operandValue, thenValue), and expanded `CaseColumn` stub to full interface (id, nodeId, alias, whens, elseValue). Added `getCaseColumnAliasError()` SSOT gate (returns i18n key when any CASE column has empty alias).
- Created `CaseColumnEditor.tsx`: floating panel (mirrors JoinInspector pattern) — alias input with inline required + FR-57 pattern validation, WHEN arms (table/column/operator/value/THEN), "Add WHEN" button, ELSE input. Live editing via `onUpdate` on every change. Operator value hidden for IS NULL / IS NOT NULL (AC-2). `z-50 absolute right-4 top-4` with scroll container.
- Updated `TableNode.tsx`: added `X` from lucide-react, `CaseColumn` type import; exported 4 new contexts (`CaseColumnsContext`, `AddCaseColumnContext`, `DeleteCaseColumnContext`, `SelectCaseColumnContext`); added context reads + `nodeCaseColumns` filter; added CASE column compact rows + "Add Case" button to node JSX footer.
- Updated `QueryBuilderCanvas.tsx`: added `caseColumns` state + `caseColumnsRef`, `selectedCaseColumnId` state; 4 handlers (handleAddCaseColumn, handleDeleteCaseColumn, handleSelectCaseColumn, handleUpdateCaseColumn) all using stable-ref posture; cascade CASE column removal in `handleNodesChange` before `notify()` call; `onPaneClick`/`onEdgeClick` mutual exclusivity; 4 new context providers; `caseColumnAliasError` banner; `selectedCaseColumn` derived + `CaseColumnEditor` render.
- Added 17 i18n keys (3 under `datasets.builder.node`, 1 under `datasets.builder.validation`, 13 under `datasets.builder.caseEditor`). All referenced by static `t()` literals.
- Added 6 unit tests for `getCaseColumnAliasError`. Added `caseCol()` helper to test file.
- Added 5 deferred-work entries (AC-3 picker, AC-4 save-blocking, AC-6 backend validator, BuilderStateDto, IN/BETWEEN UX).
- Verification: `tsc -b --noEmit` → 0 errors; `vitest run` → 310 passed / 0 failed; `i18n-check` → exit 0. No backend changes.

### File List

- `web/src/features/datasets/types/builderState.ts` (modified) — `CaseOperator`, `CASE_OPERATORS`, `CaseWhen`, expanded `CaseColumn`, `getCaseColumnAliasError`
- `web/src/features/datasets/types/__tests__/builderState.test.ts` (modified) — `getCaseColumnAliasError` + `CaseColumn` imports, `caseCol()` helper, 6-case describe block
- `web/src/components/query-builder/TableNode.tsx` (modified) — `X` + `CaseColumn` imports; 4 new contexts exported; context reads + `nodeCaseColumns` filter; CASE column rows + "Add Case" button; comment update
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (modified) — imports updated; caseColumns state + ref; selectedCaseColumnId state; 4 handlers; onPaneClick/onEdgeClick mutual exclusivity; handleNodesChange cascade; validation gate; 4 new providers; banner; CaseColumnEditor render
- `web/src/components/query-builder/CaseColumnEditor.tsx` (new) — full CASE/WHEN builder panel
- `web/src/lib/i18n/locales/en.json` (modified) — 17 new i18n keys
- `_bmad-output/implementation-artifacts/deferred-work.md` (modified) — 5 new deferral entries

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): CaseOperator/CaseWhen/CaseColumn types, CaseColumnEditor panel, 4 contexts (CaseColumnsContext/Add/Delete/Select), stable-ref handlers, cascade node-delete, getCaseColumnAliasError SSOT, validation banner, 17 i18n keys, 6 unit tests. Status → ready-for-dev. | create-story |
| 2026-06-04 | 1.0     | Implemented all 8 tasks: CaseOperator/CASE_OPERATORS/CaseWhen types + expanded CaseColumn + getCaseColumnAliasError; new CaseColumnEditor.tsx panel; 4 contexts in TableNode.tsx + CASE rows + Add Case button; full QueryBuilderCanvas.tsx wiring (state, 4 handlers, cascade node-delete, mutual exclusivity, providers, banner, editor); 17 i18n keys; 6 unit tests; deferred-work entries. Verified: tsc 0 errors, vitest 310/310, i18n-check exit 0. Status → review. | dev-story |
