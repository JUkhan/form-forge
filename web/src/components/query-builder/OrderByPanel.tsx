import { useEffect, useState } from 'react'
import { ChevronDown, ChevronUp, Plus, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import type { OrderByClause } from '../../features/datasets/types/builderState'
import type { TableNodeType } from './TableNode'

// ---------------------------------------------------------------------------
// Pure array-mutation helpers — all return a NEW array (immutable update).
// Defined at module scope and exported for unit testing (Story 10.8 Task 4).
// Flat OrderByClause[] operations — no tree recursion (unlike FilterConditionsDialog).
// Index-based addressing is correct here: OrderByClause has no `id`, and the entire
// list is replaced on every operation (no cross-position identity to preserve).
// ---------------------------------------------------------------------------

export function addOrderByClause(
  clauses: OrderByClause[],
  defaultTable: string,
  defaultColumn: string,
): OrderByClause[] {
  return [...clauses, { tableName: defaultTable, columnName: defaultColumn, direction: 'ASC' }]
}

export function removeOrderByClause(clauses: OrderByClause[], idx: number): OrderByClause[] {
  return clauses.filter((_, i) => i !== idx)
}

export function moveOrderByClause(
  clauses: OrderByClause[],
  idx: number,
  direction: 'up' | 'down',
): OrderByClause[] {
  const next = [...clauses]
  const swap = direction === 'up' ? idx - 1 : idx + 1
  if (swap >= 0 && swap < next.length) {
    ;[next[idx], next[swap]] = [next[swap], next[idx]]
  }
  return next
}

export function updateOrderByClause(
  clauses: OrderByClause[],
  idx: number,
  patch: Partial<OrderByClause>,
): OrderByClause[] {
  return clauses.map((c, i) => (i === idx ? { ...c, ...patch } : c))
}

// Co-located with FilterConditionRow.tsx by design (do NOT import across files).
const SELECT_CLASS =
  'h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring'

// ---------------------------------------------------------------------------
// OrderByPanel — Story 10.8 (FR-69 J-8). Apply/Cancel modal pattern (like
// FilterConditionsDialog): edits a local copy, emits only on Apply. NOT a live
// editing panel. Not a React Flow node, so v12 NodeProps typing rules don't apply.
// ---------------------------------------------------------------------------

interface OrderByPanelProps {
  isOpen: boolean
  orderBy: OrderByClause[]
  nodes: TableNodeType[]
  onSave: (updated: OrderByClause[]) => void
  onClose: () => void
}

export function OrderByPanel({ isOpen, orderBy, nodes, onSave, onClose }: OrderByPanelProps) {
  const { t } = useTranslation()

  // Local copy for editing — discarded on Cancel, emitted on Apply (AC-3/AC-5).
  const [localOrderBy, setLocalOrderBy] = useState<OrderByClause[]>(orderBy)

  // Sync local state when the panel re-opens with potentially new clauses from canvas.
  // Excludes 'orderBy' from deps intentionally — sync only on open, not on every
  // canvas change while the panel is open (same posture as FilterConditionsDialog).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (isOpen) setLocalOrderBy(orderBy)
  }, [isOpen])

  const firstNode = nodes.length > 0 ? nodes[0] : null
  const firstColumn = firstNode?.data.columns[0]?.columnName ?? ''

  function handleAdd() {
    setLocalOrderBy((prev) =>
      addOrderByClause(prev, firstNode?.data.tableName ?? '', firstColumn),
    )
  }

  // Reset the column to the first column of the newly-selected table (AC-2) — same
  // pattern as FilterConditionRow.handleTableChange.
  function handleTableChange(idx: number, tableName: string) {
    const firstCol =
      nodes.find((n) => n.data.tableName === tableName)?.data.columns[0]?.columnName ?? ''
    setLocalOrderBy((prev) => updateOrderByClause(prev, idx, { tableName, columnName: firstCol }))
  }

  function handleApply() {
    onSave(localOrderBy)
    onClose()
  }

  return (
    <Dialog open={isOpen} onOpenChange={(next) => { if (!next) onClose() }}>
      <DialogContent
        className="flex max-w-2xl flex-col gap-0 p-0 sm:max-w-2xl"
        style={{ maxHeight: '80vh' }}
      >
        <DialogHeader className="shrink-0 border-b border-border px-6 py-3">
          <DialogTitle>{t('datasets.builder.orderByPanel.title')}</DialogTitle>
        </DialogHeader>

        <div className="flex-1 space-y-1 overflow-y-auto p-4">
          {/* Add clause — disabled when no tables on canvas (AC-2) */}
          <button
            type="button"
            onClick={handleAdd}
            disabled={nodes.length === 0}
            title={nodes.length === 0 ? t('datasets.builder.orderByPanel.noTablesDisabled') : undefined}
            className="flex h-6 items-center gap-1 rounded border border-dashed border-border px-2 text-xs text-muted-foreground hover:border-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-40"
          >
            <Plus className="h-3 w-3" />
            {t('datasets.builder.orderByPanel.addButton')}
          </button>

          {/* Empty state (AC-5: empty is valid) */}
          {localOrderBy.length === 0 && (
            <p className="py-2 text-xs text-muted-foreground">
              {t('datasets.builder.orderByPanel.emptyState')}
            </p>
          )}

          {/* Clause rows — array order maps to ORDER BY precedence (AC-3) */}
          {localOrderBy.map((clause, idx) => {
            const tableColumns =
              nodes.find((n) => n.data.tableName === clause.tableName)?.data.columns ?? []
            return (
              <div
                key={idx}
                className="flex items-center gap-1.5 rounded border border-border bg-card px-3 py-1.5"
              >
                {/* Table selector */}
                <label className="flex flex-1 flex-col gap-0.5">
                  <span className="text-xs text-muted-foreground">
                    {t('datasets.builder.orderByPanel.tableLabel')}
                  </span>
                  <select
                    value={clause.tableName}
                    onChange={(e) => handleTableChange(idx, e.target.value)}
                    aria-label={t('datasets.builder.orderByPanel.tableLabel')}
                    className={SELECT_CLASS}
                  >
                    {nodes.map((n) => (
                      <option key={n.id} value={n.data.tableName}>
                        {n.data.tableName}
                      </option>
                    ))}
                  </select>
                </label>

                {/* Column selector */}
                <label className="flex flex-1 flex-col gap-0.5">
                  <span className="text-xs text-muted-foreground">
                    {t('datasets.builder.orderByPanel.columnLabel')}
                  </span>
                  <select
                    value={clause.columnName}
                    onChange={(e) =>
                      setLocalOrderBy((prev) =>
                        updateOrderByClause(prev, idx, { columnName: e.target.value }),
                      )
                    }
                    aria-label={t('datasets.builder.orderByPanel.columnLabel')}
                    disabled={tableColumns.length === 0}
                    className={SELECT_CLASS}
                  >
                    {tableColumns.length === 0 ? (
                      <option value="">{t('datasets.builder.orderByPanel.noColumnsOption')}</option>
                    ) : (
                      tableColumns.map((c) => (
                        <option key={c.columnName} value={c.columnName}>
                          {c.columnName}
                        </option>
                      ))
                    )}
                  </select>
                </label>

                {/* Direction selector (ASC / DESC) */}
                <label className="flex w-24 flex-col gap-0.5">
                  <span className="text-xs text-muted-foreground">
                    {t('datasets.builder.orderByPanel.directionLabel')}
                  </span>
                  <select
                    value={clause.direction}
                    onChange={(e) =>
                      setLocalOrderBy((prev) =>
                        updateOrderByClause(prev, idx, {
                          direction: e.target.value as 'ASC' | 'DESC',
                        }),
                      )
                    }
                    aria-label={t('datasets.builder.orderByPanel.directionLabel')}
                    className={SELECT_CLASS}
                  >
                    <option value="ASC">{t('datasets.builder.orderByPanel.directionAsc')}</option>
                    <option value="DESC">{t('datasets.builder.orderByPanel.directionDesc')}</option>
                  </select>
                </label>

                {/* Move up / down — precedence reorder (AC-3) */}
                <button
                  type="button"
                  onClick={() => setLocalOrderBy((prev) => moveOrderByClause(prev, idx, 'up'))}
                  disabled={idx === 0}
                  aria-label={t('datasets.builder.orderByPanel.moveUpAria')}
                  className="mt-[1.125rem] flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:opacity-30"
                >
                  <ChevronUp className="h-3 w-3" />
                </button>
                <button
                  type="button"
                  onClick={() => setLocalOrderBy((prev) => moveOrderByClause(prev, idx, 'down'))}
                  disabled={idx === localOrderBy.length - 1}
                  aria-label={t('datasets.builder.orderByPanel.moveDownAria')}
                  className="mt-[1.125rem] flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:opacity-30"
                >
                  <ChevronDown className="h-3 w-3" />
                </button>

                {/* Delete clause */}
                <button
                  type="button"
                  onClick={() => setLocalOrderBy((prev) => removeOrderByClause(prev, idx))}
                  aria-label={t('datasets.builder.orderByPanel.deleteClauseAria')}
                  className="mt-[1.125rem] flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                >
                  <X className="h-3 w-3" />
                </button>
              </div>
            )
          })}
        </div>

        <DialogFooter className="shrink-0 gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-border bg-card px-4 py-2 text-sm font-medium text-foreground hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {t('common.cancel')}
          </button>
          <button
            type="button"
            onClick={handleApply}
            className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {t('datasets.builder.orderByPanel.saveButton')}
          </button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
