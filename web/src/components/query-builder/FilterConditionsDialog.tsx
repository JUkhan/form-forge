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
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import type {
  FilterCondition,
  FilterGroup,
  FilterItem,
} from '../../features/datasets/types/builderState'
import type { TableNodeType } from './TableNode'
import { FilterConditionRow } from './FilterConditionRow'

// Module-level sequence — never resets across dialog open/close cycles.
// Prevents ID collision when a user adds items, closes the dialog (saves), and reopens.
let _filterIdSeq = 0
function nextFilterId(kind: 'cond' | 'grp'): string {
  return `${kind}-${++_filterIdSeq}`
}

// Story 11.2 (FR-71 AC-2): called from QueryBuilderCanvas on mount (after
// parseBuilderState) to ensure nextFilterId() never re-uses an id from a restored filter
// tree. Bumps the module-level _filterIdSeq to the max numeric suffix found in the tree.
// Idempotent — only ever increases the counter, so it is safe to call multiple times.
export function seedFilterIdCounter(group: FilterGroup): void {
  function walkMax(g: FilterGroup): number {
    return g.items.reduce((max, item) => {
      const m = item.id.match(/-(\d+)$/)
      const here = m ? parseInt(m[1], 10) : 0
      const sub = item.kind === 'group' ? walkMax(item) : 0
      return Math.max(max, here, sub)
    }, 0)
  }
  const found = walkMax(group)
  if (found > _filterIdSeq) _filterIdSeq = found
}

// ---------------------------------------------------------------------------
// Pure tree-mutation helpers — all return a new FilterGroup (immutable update).
// Defined at module scope so they are stable references (no useCallback needed).
// ---------------------------------------------------------------------------

export function treeSetCombinator(
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

export function treeAddItem(root: FilterGroup, groupId: string, item: FilterItem): FilterGroup {
  if (root.id === groupId) return { ...root, items: [...root.items, item] }
  return {
    ...root,
    items: root.items.map((i) => (i.kind === 'group' ? treeAddItem(i, groupId, item) : i)),
  }
}

export function treeRemoveItem(root: FilterGroup, itemId: string): FilterGroup {
  return {
    ...root,
    items: root.items
      .filter((i) => i.id !== itemId)
      .map((i) => (i.kind === 'group' ? treeRemoveItem(i, itemId) : i)),
  }
}

export function treeMoveItem(
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

// Story 10.6: replace a condition (matched by id) with an updated copy.
// Recurses into sub-groups so a condition at any depth is found.
export function treeUpdateCondition(root: FilterGroup, updated: FilterCondition): FilterGroup {
  return {
    ...root,
    items: root.items.map((item) =>
      item.kind === 'condition' && item.id === updated.id
        ? updated
        : item.kind === 'group'
          ? treeUpdateCondition(item, updated)
          : item,
    ),
  }
}

// Depth-level border colors — cycles every 4 levels to visually distinguish nesting.
// Must be full class strings (not dynamic interpolation) for Tailwind JIT.
const DEPTH_BORDER_COLORS = [
  'border-blue-500/40',
  'border-violet-500/40',
  'border-emerald-500/40',
  'border-amber-500/40',
] as const

// ---------------------------------------------------------------------------
// FilterGroupContent — recursive sub-component for one level of the tree.
// ---------------------------------------------------------------------------

interface FilterGroupContentProps {
  group: FilterGroup
  depth: number // 0 = root, 1+ = sub-groups
  isRoot: boolean
  nodes: TableNodeType[]
  onSetCombinator: (groupId: string, combinator: 'AND' | 'OR') => void
  onAddCondition: (groupId: string) => void
  onAddGroup: (groupId: string) => void
  onDeleteItem: (itemId: string) => void
  onMoveItem: (itemId: string, direction: 'up' | 'down') => void
  onUpdateCondition: (updated: FilterCondition) => void
}

function FilterGroupContent({
  group,
  depth,
  // isRoot is reserved for forward-compatibility (e.g. disabling delete on the root
  // group). It is passed by callers but intentionally not consumed in render yet.
  nodes,
  onSetCombinator,
  onAddCondition,
  onAddGroup,
  onDeleteItem,
  onMoveItem,
  onUpdateCondition,
}: FilterGroupContentProps) {
  const { t } = useTranslation()
  const [addOpen, setAddOpen] = useState(false)

  // Indent each depth level with depth-cycling border color for visual nesting (AC-2).
  const indentClass =
    depth > 0
      ? `pl-4 border-l-2 ${DEPTH_BORDER_COLORS[(depth - 1) % DEPTH_BORDER_COLORS.length]}`
      : ''

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

          {/* Condition row — table/column/operator/value inputs (Story 10.6) */}
          {item.kind === 'condition' && (
            <FilterConditionRow
              condition={item}
              nodes={nodes}
              onChange={onUpdateCondition}
              onDelete={() => onDeleteItem(item.id)}
            />
          )}

          {/* Sub-group (recursive — AC-4) */}
          {item.kind === 'group' && (
            <div className="flex flex-1 flex-col gap-1">
              <FilterGroupContent
                group={item}
                depth={depth + 1}
                isRoot={false}
                nodes={nodes}
                onSetCombinator={onSetCombinator}
                onAddCondition={onAddCondition}
                onAddGroup={onAddGroup}
                onDeleteItem={onDeleteItem}
                onMoveItem={onMoveItem}
                onUpdateCondition={onUpdateCondition}
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
  nodes: TableNodeType[]
  onSave: (filters: FilterGroup) => void
  onClose: () => void
}

export function FilterConditionsDialog({
  isOpen,
  filters,
  nodes,
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
    // Seed the new condition with the first available table/column so the row
    // renders meaningful selects immediately (Story 10.6). Operator defaults to
    // '=', whose empty value shape is '' (a single string).
    const firstNode = nodes.length > 0 ? nodes[0] : null
    const firstCol = firstNode?.data.columns[0]?.columnName ?? ''
    const newCond: FilterCondition = {
      id: nextFilterId('cond'),
      kind: 'condition',
      tableName: firstNode?.data.tableName ?? '',
      columnName: firstCol,
      operator: '=',
      value: '',
    }
    setLocalFilters((prev) => treeAddItem(prev, groupId, newCond))
  }

  function handleUpdateCondition(updated: FilterCondition) {
    setLocalFilters((prev) => treeUpdateCondition(prev, updated))
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
            nodes={nodes}
            onSetCombinator={handleSetCombinator}
            onAddCondition={handleAddCondition}
            onAddGroup={handleAddGroup}
            onDeleteItem={handleDeleteItem}
            onMoveItem={handleMoveItem}
            onUpdateCondition={handleUpdateCondition}
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
