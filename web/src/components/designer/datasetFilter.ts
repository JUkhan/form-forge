// DatasetComponent filter model. A fieldKey-bound variant of the Query Builder's filter
// tree (web/src/features/datasets/types/builderState.ts): each condition pairs a dataset
// column + operator with a filter-input's `fieldKey` (the value is supplied at runtime by
// that input, not typed at design time). Phase 1 keeps a single root group (flat list of
// conditions joined by one AND/OR) — the group shape is forward-compatible with nesting.

import type { FilterOperator } from '@/features/datasets/types/builderState'

export type DatasetFilterCondition = {
  id: string
  kind: 'condition'
  columnName: string
  operator: FilterOperator
  valueFieldKey: string // '' until bound; unused for IS NULL / IS NOT NULL
}

export type DatasetFilterGroup = {
  id: string
  kind: 'group'
  combinator: 'AND' | 'OR'
  items: DatasetFilterCondition[]
}

// Operators that take no value (and therefore need no bound fieldKey).
export const NO_VALUE_OPERATORS: ReadonlySet<FilterOperator> = new Set<FilterOperator>([
  'IS NULL',
  'IS NOT NULL',
])

// Operators whose bound input value is a comma-separated list.
const LIST_OPERATORS: ReadonlySet<FilterOperator> = new Set<FilterOperator>(['IN', 'NOT IN'])

export const EMPTY_DATASET_FILTER: DatasetFilterGroup = {
  id: 'root',
  kind: 'group',
  combinator: 'AND',
  items: [],
}

let idCounter = 0
export function newConditionId(): string {
  idCounter += 1
  return `dfc-${idCounter}`
}

// Normalize a persisted/unknown value into a well-formed DatasetFilterGroup. Anything
// malformed collapses to an empty group so the inspector never crashes on bad data.
export function parseDatasetFilter(raw: unknown): DatasetFilterGroup {
  if (raw === null || typeof raw !== 'object') return { ...EMPTY_DATASET_FILTER, items: [] }
  const obj = raw as Record<string, unknown>
  const combinator = obj.combinator === 'OR' ? 'OR' : 'AND'
  const rawItems = Array.isArray(obj.items) ? obj.items : []
  const items: DatasetFilterCondition[] = []
  for (const it of rawItems) {
    if (it === null || typeof it !== 'object') continue
    const c = it as Record<string, unknown>
    if (c.kind !== 'condition') continue // Phase 1: ignore nested groups
    items.push({
      id: typeof c.id === 'string' && c.id !== '' ? c.id : newConditionId(),
      kind: 'condition',
      columnName: typeof c.columnName === 'string' ? c.columnName : '',
      operator: (typeof c.operator === 'string' ? c.operator : '=') as FilterOperator,
      valueFieldKey: typeof c.valueFieldKey === 'string' ? c.valueFieldKey : '',
    })
  }
  return { id: 'root', kind: 'group', combinator, items }
}

// ── immutable editor ops ─────────────────────────────────────────────────────────────

export function addCondition(group: DatasetFilterGroup): DatasetFilterGroup {
  return {
    ...group,
    items: [
      ...group.items,
      { id: newConditionId(), kind: 'condition', columnName: '', operator: '=', valueFieldKey: '' },
    ],
  }
}

export function removeCondition(group: DatasetFilterGroup, id: string): DatasetFilterGroup {
  return { ...group, items: group.items.filter((c) => c.id !== id) }
}

export function updateCondition(
  group: DatasetFilterGroup,
  id: string,
  patch: Partial<Omit<DatasetFilterCondition, 'id' | 'kind'>>,
): DatasetFilterGroup {
  return {
    ...group,
    items: group.items.map((c) => (c.id === id ? { ...c, ...patch } : c)),
  }
}

export function setCombinator(group: DatasetFilterGroup, combinator: 'AND' | 'OR'): DatasetFilterGroup {
  return { ...group, combinator }
}

// ── runtime resolution ───────────────────────────────────────────────────────────────

// A resolved condition matching the backend FilterConditionDto shape (tableName unused for
// a single VIEW). value: string for scalar ops, string[] for IN/NOT IN/BETWEEN, null for
// IS NULL / IS NOT NULL.
export type ResolvedCondition = {
  id: string
  kind: 'condition'
  tableName: string
  columnName: string
  operator: FilterOperator
  value: string | string[] | null
}

export type ResolvedFilterGroup = {
  id: string
  kind: 'group'
  combinator: 'AND' | 'OR'
  items: ResolvedCondition[]
}

// Resolve the design-time conditions against the live filter-input values. Conditions with
// no column, or whose value-bearing operator has an empty/missing bound value, are pruned
// (an empty filter must not constrain the result). Returns null when nothing remains, so
// callers can omit `filters` from the request entirely.
export function resolveDatasetFilter(
  group: DatasetFilterGroup | null | undefined,
  values: Record<string, unknown>,
): ResolvedFilterGroup | null {
  if (!group || group.items.length === 0) return null
  const items: ResolvedCondition[] = []
  for (const c of group.items) {
    if (c.columnName === '') continue

    if (NO_VALUE_OPERATORS.has(c.operator)) {
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: null })
      continue
    }

    const raw = c.valueFieldKey !== '' ? values[c.valueFieldKey] : undefined
    if (raw === undefined || raw === null || raw === '') continue
    const str = String(raw)

    if (LIST_OPERATORS.has(c.operator)) {
      const parts = str.split(',').map((s) => s.trim()).filter((s) => s !== '')
      if (parts.length === 0) continue
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: parts })
      continue
    }

    if (c.operator === 'BETWEEN') {
      const parts = str.split(',').map((s) => s.trim())
      if (parts.length < 2 || parts[0] === '' || parts[1] === '') continue
      items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: 'BETWEEN', value: [parts[0], parts[1]] })
      continue
    }

    items.push({ id: c.id, kind: 'condition', tableName: '', columnName: c.columnName, operator: c.operator, value: str })
  }
  if (items.length === 0) return null
  return { id: group.id, kind: 'group', combinator: group.combinator, items }
}
