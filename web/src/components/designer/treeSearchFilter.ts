// TreeView "entire tree search" filter model. A value-less sibling of the DatasetComponent
// filter (web/src/components/designer/datasetFilter.ts): each condition pairs a column +
// operator, but the value side is NOT bound to a filter-input fieldKey. Instead, every
// condition is resolved against the SINGLE literal typed into the TreeView's search box at
// runtime. Like the dataset filter it keeps one flat root group (AND/OR), forward-compatible
// with nesting, and resolves to the same backend FilterConditionDto shape so the recursive
// SEARCH_CONDITIONS query can reuse DatasetFilterWhereBuilder.

import { uid } from '@/lib/utils'
import type { FilterOperator } from '@/features/datasets/types/builderState'
import {
  NO_VALUE_OPERATORS,
  type ResolvedCondition,
  type ResolvedFilterGroup,
} from './datasetFilter'

export type TreeSearchCondition = {
  id: string
  columnName: string
  operator: FilterOperator
}

export type TreeSearchGroup = {
  combinator: 'AND' | 'OR'
  items: TreeSearchCondition[]
}

// Operators whose runtime value is a comma-separated list (mirrors datasetFilter's private
// set; the search box supplies the raw string which is split here).
const LIST_OPERATORS: ReadonlySet<FilterOperator> = new Set<FilterOperator>(['IN', 'NOT IN'])

export const EMPTY_TREE_SEARCH_FILTER: TreeSearchGroup = { combinator: 'AND', items: [] }

export function newTreeSearchConditionId(): string {
  return uid('tsc')
}

// Normalize a persisted/unknown value (DesignerElementProperties.treeSearchConditions) into a
// well-formed group. Anything malformed collapses to an empty group so the inspector never
// crashes on bad data — same defensive posture as parseDatasetFilter.
export function parseTreeSearchFilter(raw: unknown): TreeSearchGroup {
  if (raw === null || typeof raw !== 'object') return { ...EMPTY_TREE_SEARCH_FILTER, items: [] }
  const obj = raw as Record<string, unknown>
  const combinator = obj.combinator === 'OR' ? 'OR' : 'AND'
  const rawItems = Array.isArray(obj.items) ? obj.items : []
  const items: TreeSearchCondition[] = []
  for (const it of rawItems) {
    if (it === null || typeof it !== 'object') continue
    const c = it as Record<string, unknown>
    items.push({
      id: typeof c.id === 'string' && c.id !== '' ? c.id : newTreeSearchConditionId(),
      columnName: typeof c.columnName === 'string' ? c.columnName : '',
      operator: (typeof c.operator === 'string' ? c.operator : '=') as FilterOperator,
    })
  }
  return { combinator, items }
}

// ── immutable editor ops ─────────────────────────────────────────────────────────────

export function addCondition(group: TreeSearchGroup): TreeSearchGroup {
  return {
    ...group,
    items: [...group.items, { id: newTreeSearchConditionId(), columnName: '', operator: '=' }],
  }
}

export function removeCondition(group: TreeSearchGroup, id: string): TreeSearchGroup {
  return { ...group, items: group.items.filter((c) => c.id !== id) }
}

export function updateCondition(
  group: TreeSearchGroup,
  id: string,
  patch: Partial<Omit<TreeSearchCondition, 'id'>>,
): TreeSearchGroup {
  return { ...group, items: group.items.map((c) => (c.id === id ? { ...c, ...patch } : c)) }
}

export function setCombinator(group: TreeSearchGroup, combinator: 'AND' | 'OR'): TreeSearchGroup {
  return { ...group, combinator }
}

// ── runtime resolution ───────────────────────────────────────────────────────────────

// Resolve the design-time conditions against the ONE literal typed in the search box,
// producing the same ResolvedFilterGroup the dataset filter emits (so the backend reuses
// DatasetFilterWhereBuilder). Conditions with no column are pruned. Value-bearing operators
// are pruned when the search box is empty (an empty search must not constrain). IN/NOT IN
// split the literal on commas; BETWEEN needs two comma-separated parts. Returns null when
// nothing remains so callers can skip the search request entirely.
export function resolveTreeSearchFilter(
  group: TreeSearchGroup | null | undefined,
  search: string,
): ResolvedFilterGroup | null {
  if (!group || group.items.length === 0) return null
  const trimmed = search.trim()
  const items: ResolvedCondition[] = []
  for (const c of group.items) {
    if (c.columnName === '') continue

    if (NO_VALUE_OPERATORS.has(c.operator)) {
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: null })
      continue
    }

    if (trimmed === '') continue

    if (LIST_OPERATORS.has(c.operator)) {
      const parts = trimmed.split(',').map((s) => s.trim()).filter((s) => s !== '')
      if (parts.length === 0) continue
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: parts })
      continue
    }

    if (c.operator === 'BETWEEN') {
      const parts = trimmed.split(',').map((s) => s.trim())
      if (parts.length < 2 || parts[0] === '' || parts[1] === '') continue
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: 'BETWEEN', value: [parts[0], parts[1]] })
      continue
    }

    items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: trimmed })
  }
  if (items.length === 0) return null
  return { id: 'root', kind: 'group', combinator: group.combinator, items }
}
