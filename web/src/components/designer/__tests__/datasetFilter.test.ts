import { describe, it, expect } from 'vitest'
import {
  addCondition,
  EMPTY_DATASET_FILTER,
  parseDatasetFilter,
  removeCondition,
  resolveDatasetFilter,
  setCombinator,
  splitResolvedFilter,
  updateCondition,
  type DatasetFilterGroup,
} from '../datasetFilter'

function groupWith(items: DatasetFilterGroup['items']): DatasetFilterGroup {
  return { id: 'root', kind: 'group', combinator: 'AND', items }
}

describe('parseDatasetFilter', () => {
  it('returns an empty group for malformed input', () => {
    expect(parseDatasetFilter(null).items).toEqual([])
    expect(parseDatasetFilter(undefined).items).toEqual([])
    expect(parseDatasetFilter('nope').items).toEqual([])
  })

  it('normalizes a stored group and ignores nested groups (Phase 1)', () => {
    const parsed = parseDatasetFilter({
      id: 'root',
      kind: 'group',
      combinator: 'OR',
      items: [
        { id: 'c1', kind: 'condition', columnName: 'status', operator: '=', valueFieldKey: 'st' },
        { id: 'g1', kind: 'group', combinator: 'AND', items: [] },
      ],
    })
    expect(parsed.combinator).toBe('OR')
    expect(parsed.items).toHaveLength(1)
    expect(parsed.items[0].columnName).toBe('status')
  })
})

describe('editor ops', () => {
  it('adds, updates, removes, and sets combinator immutably', () => {
    let g = EMPTY_DATASET_FILTER
    g = addCondition(g)
    expect(g.items).toHaveLength(1)
    const id = g.items[0].id
    g = updateCondition(g, id, { columnName: 'dept', operator: 'IN', valueFieldKey: 'd' })
    expect(g.items[0]).toMatchObject({ columnName: 'dept', operator: 'IN', valueFieldKey: 'd' })
    g = setCombinator(g, 'OR')
    expect(g.combinator).toBe('OR')
    g = removeCondition(g, id)
    expect(g.items).toHaveLength(0)
  })
})

describe('resolveDatasetFilter', () => {
  it('returns null when nothing is bound or values are empty', () => {
    expect(resolveDatasetFilter(null, {})).toBeNull()
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'name', operator: '=', valueFieldKey: 'q' },
    ])
    expect(resolveDatasetFilter(g, {})).toBeNull() // q unset → pruned → empty
    expect(resolveDatasetFilter(g, { q: '' })).toBeNull()
  })

  it('resolves a scalar condition to a literal value', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'name', operator: '=', valueFieldKey: 'q' },
    ])
    const r = resolveDatasetFilter(g, { q: 'Alice' })
    expect(r).not.toBeNull()
    expect(r!.items[0]).toMatchObject({ columnName: 'name', operator: '=', value: 'Alice', tableName: '' })
  })

  it('keeps IS NULL conditions without needing a bound value', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'deleted_at', operator: 'IS NULL', valueFieldKey: '' },
    ])
    const r = resolveDatasetFilter(g, {})
    expect(r!.items[0]).toMatchObject({ operator: 'IS NULL', value: null })
  })

  it('splits IN values on commas and BETWEEN into a pair', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'dept', operator: 'IN', valueFieldKey: 'd' },
      { id: 'c2', kind: 'condition', columnName: 'salary', operator: 'BETWEEN', valueFieldKey: 's' },
    ])
    const r = resolveDatasetFilter(g, { d: 'Eng, Sales ,Ops', s: '50, 100' })
    expect(r!.items[0].value).toEqual(['Eng', 'Sales', 'Ops'])
    expect(r!.items[1].value).toEqual(['50', '100'])
  })

  it('prunes a BETWEEN with fewer than two values', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'salary', operator: 'BETWEEN', valueFieldKey: 's' },
    ])
    expect(resolveDatasetFilter(g, { s: '50' })).toBeNull()
  })
})

describe('splitResolvedFilter (parameterized queries)', () => {
  it('passes the filter through unchanged when there are no placeholders', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: 'name', operator: '=', valueFieldKey: 'q' },
    ])
    const resolved = resolveDatasetFilter(g, { q: 'Alice' })
    const { filters, queryParameters } = splitResolvedFilter(resolved, [])
    expect(filters).toBe(resolved)
    expect(queryParameters).toBeUndefined()
  })

  it('routes placeholder conditions into queryParameters and keeps the rest as filters', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: '_age', operator: '=', valueFieldKey: 'age' },
      { id: 'c2', kind: 'condition', columnName: 'dept', operator: '=', valueFieldKey: 'd' },
    ])
    const resolved = resolveDatasetFilter(g, { age: '23', d: 'Eng' })
    const { filters, queryParameters } = splitResolvedFilter(resolved, ['_age'])

    expect(queryParameters).toBe(JSON.stringify({ _age: '23' }))
    expect(filters).not.toBeNull()
    expect(filters!.items).toHaveLength(1)
    expect(filters!.items[0].columnName).toBe('dept')
  })

  it('returns null filters when every condition is a placeholder binding', () => {
    const g = groupWith([
      { id: 'c1', kind: 'condition', columnName: '_age', operator: '=', valueFieldKey: 'age' },
      { id: 'c2', kind: 'condition', columnName: '_name', operator: '=', valueFieldKey: 'nm' },
    ])
    const resolved = resolveDatasetFilter(g, { age: '23', nm: 'Carol' })
    const { filters, queryParameters } = splitResolvedFilter(resolved, ['_age', '_name'])

    expect(filters).toBeNull()
    expect(queryParameters).toBe(JSON.stringify({ _age: '23', _name: 'Carol' }))
  })

  it('handles a null resolved filter', () => {
    const { filters, queryParameters } = splitResolvedFilter(null, ['_age'])
    expect(filters).toBeNull()
    expect(queryParameters).toBeUndefined()
  })
})
