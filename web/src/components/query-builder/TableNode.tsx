import { X } from 'lucide-react'
import { memo, createContext, useContext } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  type AggregateFunction,
  type CaseColumn,
  type CalculatedColumn,
  type TableNodeData,
  type TableSide,
} from '../../features/datasets/types/builderState'

// The React Flow node type carried on the canvas. v12 keys NodeProps on the Node
// type (not its data), so we declare it explicitly: Node<data, typeDiscriminator>.
export type TableNodeType = Node<TableNodeData, 'tableNode'>

// Story 9.5: the canvas provides the side-change handler via context so it can
// own the single-Left invariant and emit to the parent via notify(). Kept out of
// node `data` because `data` is serialized into builder_state (must stay pure).
// Default is a no-op so the node renders safely outside the provider (tests).
export const TableSideChangeContext = createContext<(nodeId: string, side: TableSide) => void>(
  () => {},
)

// Story 10.1: the canvas provides the column-check handler via context so it can
// update a specific column's `checked` flag and emit to the parent via notify().
// Kept out of node `data` because `data` is serialized into builder_state (must
// stay pure — functions are not serializable). Default is a no-op for safety.
export const ColumnCheckContext = createContext<
  (nodeId: string, columnName: string, checked: boolean) => void
>(() => {})

// Story 10.2: canvas provides aggregate-change handler via context so it can emit
// to the parent via notify(). Same stable-ref pattern as ColumnCheckContext — kept
// out of node `data` because data is serialized into builder_state (not serializable).
export const ColumnAggregateContext = createContext<
  (nodeId: string, columnName: string, aggregate: AggregateFunction) => void
>(() => {})

// Story 10.2: canvas provides alias-change handler via context. Same rationale.
export const ColumnAliasContext = createContext<
  (nodeId: string, columnName: string, alias: string) => void
>(() => {})

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

// Story 9.1: Structural shell. Story 9.5 makes the Left/Right toggle interactive.
// Story 10.1 makes column checkboxes interactive via ColumnCheckContext.
// Story 10.2 adds aggregate dropdown + alias input for checked columns.
// Story 10.3 adds CASE column rows + "Add Case" button (CaseColumnsContext,
//   AddCaseColumnContext, DeleteCaseColumnContext, SelectCaseColumnContext).
// Story 10.4 adds calculated column rows + "Add Calculated Column" button
//   (CalculatedColumnsContext, AddCalculatedColumnContext,
//    DeleteCalculatedColumnContext, SelectCalculatedColumnContext).
export const TableNode = memo(function TableNode({ id, data, selected }: NodeProps<TableNodeType>) {
  const { t } = useTranslation()
  const onSideChange = useContext(TableSideChangeContext)
  const onColumnCheck = useContext(ColumnCheckContext)
  const onColumnAggregate = useContext(ColumnAggregateContext)
  const onColumnAlias = useContext(ColumnAliasContext)
  const caseColumns = useContext(CaseColumnsContext)
  const onAddCaseColumn = useContext(AddCaseColumnContext)
  const onDeleteCaseColumn = useContext(DeleteCaseColumnContext)
  const onSelectCaseColumn = useContext(SelectCaseColumnContext)
  const nodeCaseColumns = caseColumns.filter((c) => c.nodeId === id)
  const calculatedColumns = useContext(CalculatedColumnsContext)
  const onAddCalculatedColumn = useContext(AddCalculatedColumnContext)
  const onDeleteCalculatedColumn = useContext(DeleteCalculatedColumnContext)
  const onSelectCalculatedColumn = useContext(SelectCalculatedColumnContext)
  // Filter to this node's calculated columns only
  const nodeCalculatedColumns = calculatedColumns.filter((c) => c.nodeId === id)
  return (
    <div
      className={`min-w-[240px] rounded-lg border bg-card text-card-foreground shadow-sm ${
        selected ? 'ring-2 ring-ring' : ''
      }`}
    >
      {/* Header */}
      <div className="flex items-center justify-between rounded-t-lg bg-muted px-3 py-2">
        <span className="truncate text-sm font-semibold">{data.tableName}</span>
        {/* Left/Right designation — interactive (Story 9.5). `nodrag nopan` so
            clicking a button never starts a node drag or canvas pan. */}
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
      </div>

      {/* Column list — Story 10.1: checkboxes; Story 10.2: aggregate + alias for checked columns */}
      <div className="divide-y divide-border">
        {data.columns.map((col) => {
          const aliasError = col.alias !== '' && !isValidAlias(col.alias)
          // Programmatically link the inline error to the input so screen readers
          // announce the reason (aria-invalid alone signals only that it's invalid).
          const aliasErrorId = `${id}-${col.columnName}-alias-error`
          return (
            <div key={col.columnName} className="relative">
              {/* Main column row: target handle (left), checkbox, name, pgType, source
                  handle (right) — conventional input-left / output-right layout. The
                  canvas uses ConnectionMode.Loose, so a join can be drawn from either
                  handle to any column handle on another node. Both handles share the
                  column name as their id because the server JOIN generation reads the
                  edge's source/target handle ids as the join column names. */}
              <div className="flex items-center gap-2 px-3 py-1.5">
                <Handle
                  type="target"
                  position={Position.Left}
                  id={col.columnName}
                  className="!h-2 !w-2 !border-border !bg-muted-foreground"
                />
                <input
                  type="checkbox"
                  checked={col.checked}
                  onChange={(e) => onColumnCheck(id, col.columnName, e.target.checked)}
                  aria-label={col.columnName}
                  className="nodrag nopan h-3.5 w-3.5 cursor-pointer"
                />
                <span className="min-w-0 flex-1 truncate text-xs">{col.columnName}</span>
                <span className="shrink-0 text-xs text-muted-foreground">{col.pgType}</span>
                <Handle
                  type="source"
                  position={Position.Right}
                  id={col.columnName}
                  className="!h-2 !w-2 !border-border !bg-muted-foreground"
                />
              </div>
              {/* Story 10.2: aggregate + alias row, visible only for checked columns */}
              {col.checked && (
                <div className="nodrag nopan flex flex-col gap-1 px-3 pb-2">
                  <div className="flex gap-1.5">
                    <select
                      value={col.aggregate}
                      onChange={(e) =>
                        onColumnAggregate(id, col.columnName, e.target.value as AggregateFunction)
                      }
                      aria-label={`${col.columnName} aggregate`}
                      className="nodrag nopan h-6 shrink-0 cursor-pointer rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    >
                      <option value="none">None</option>
                      <option value="COUNT">COUNT</option>
                      <option value="SUM">SUM</option>
                      <option value="AVG">AVG</option>
                      <option value="MIN">MIN</option>
                      <option value="MAX">MAX</option>
                    </select>
                    <input
                      type="text"
                      value={col.alias}
                      onChange={(e) => onColumnAlias(id, col.columnName, e.target.value)}
                      placeholder={`${data.tableName}_${col.columnName}`}
                      aria-label={`${col.columnName} alias`}
                      aria-invalid={aliasError}
                      aria-describedby={aliasError ? aliasErrorId : undefined}
                      className={`nodrag nopan h-6 min-w-0 flex-1 rounded border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
                        aliasError ? 'border-destructive' : 'border-border'
                      }`}
                    />
                  </div>
                  {aliasError && (
                    <p id={aliasErrorId} className="text-xs text-destructive">
                      {t('datasets.builder.node.aliasInvalidPattern')}
                    </p>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>

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
    </div>
  )
})
