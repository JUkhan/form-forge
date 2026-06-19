import { describe, it, expect } from 'vitest'
import {
  addCondition,
  EMPTY_TREE_SEARCH_FILTER,
  parseTreeSearchFilter,
  removeCondition,
  resolveTreeSearchFilter,
  setCombinator,
  updateCondition,
  type TreeSearchGroup,
} from '../treeSearchFilter'

function groupWith(items: TreeSearchGroup['items']): TreeSearchGroup {
  return { combinator: 'AND', items }
}

describe('parseTreeSearchFilter', () => {
  it('returns an empty group for malformed input', () => {
    expect(parseTreeSearchFilter(null).items).toEqual([])
    expect(parseTreeSearchFilter(undefined).items).toEqual([])
    expect(parseTreeSearchFilter('nope').items).toEqual([])
  })

  it('normalizes a stored group (no valueFieldKey — value comes from the search box)', () => {
    const parsed = parseTreeSearchFilter({
      combinator: 'OR',
      items: [
        { id: 'c1', columnName: 'name', operator: 'ILIKE' },
        { id: '', columnName: 'code', operator: '=' }, // missing id → generated
      ],
    })
    expect(parsed.combinator).toBe('OR')
    expect(parsed.items).toHaveLength(2)
    expect(parsed.items[0]).toMatchObject({ columnName: 'name', operator: 'ILIKE' })
    expect(parsed.items[0]).not.toHaveProperty('valueFieldKey')
    expect(parsed.items[1].id).not.toBe('') // a fresh id was assigned
  })
})

describe('editor ops', () => {
  it('adds, updates, removes, and sets combinator immutably', () => {
    let g = EMPTY_TREE_SEARCH_FILTER
    g = addCondition(g)
    expect(g.items).toHaveLength(1)
    const id = g.items[0].id
    g = updateCondition(g, id, { columnName: 'dept', operator: 'IN' })
    expect(g.items[0]).toMatchObject({ columnName: 'dept', operator: 'IN' })
    g = setCombinator(g, 'OR')
    expect(g.combinator).toBe('OR')
    g = removeCondition(g, id)
    expect(g.items).toHaveLength(0)
  })
})

describe('resolveTreeSearchFilter', () => {
  it('returns null with no conditions or an empty search literal', () => {
    expect(resolveTreeSearchFilter(null, 'foo')).toBeNull()
    expect(resolveTreeSearchFilter(EMPTY_TREE_SEARCH_FILTER, 'foo')).toBeNull()
    const g = groupWith([{ id: 'c1', columnName: 'name', operator: 'ILIKE' }])
    expect(resolveTreeSearchFilter(g, '')).toBeNull() // empty search → value-op pruned
    expect(resolveTreeSearchFilter(g, '   ')).toBeNull()
  })

  it('binds every value-bearing condition to the single search literal', () => {
    const g = groupWith([
      { id: 'c1', columnName: 'name', operator: 'ILIKE' },
      { id: 'c2', columnName: 'code', operator: '=' },
    ])
    const r = resolveTreeSearchFilter(g, 'Alice')
    expect(r).not.toBeNull()
    expect(r!.combinator).toBe('AND')
    expect(r!.items[0]).toMatchObject({ columnName: 'name', operator: 'ILIKE', value: 'Alice', tableName: '' })
    expect(r!.items[1]).toMatchObject({ columnName: 'code', operator: '=', value: 'Alice' })
  })

  it('keeps IS NULL / IS NOT NULL even when the search box is empty', () => {
    const g = groupWith([{ id: 'c1', columnName: 'deleted_at', operator: 'IS NULL' }])
    const r = resolveTreeSearchFilter(g, '')
    expect(r!.items[0]).toMatchObject({ operator: 'IS NULL', value: null })
  })

  it('prunes conditions with no column, but keeps the rest', () => {
    const g = groupWith([
      { id: 'c1', columnName: '', operator: '=' },
      { id: 'c2', columnName: 'name', operator: '=' },
    ])
    const r = resolveTreeSearchFilter(g, 'x')
    expect(r!.items).toHaveLength(1)
    expect(r!.items[0].columnName).toBe('name')
  })

  it('splits the literal for IN and BETWEEN', () => {
    const list = groupWith([{ id: 'c1', columnName: 'dept', operator: 'IN' }])
    expect(resolveTreeSearchFilter(list, 'Eng, Sales ,Ops')!.items[0].value).toEqual(['Eng', 'Sales', 'Ops'])

    const between = groupWith([{ id: 'c1', columnName: 'salary', operator: 'BETWEEN' }])
    expect(resolveTreeSearchFilter(between, '50, 100')!.items[0].value).toEqual(['50', '100'])
    // A BETWEEN with fewer than two parts is pruned → nothing left → null.
    expect(resolveTreeSearchFilter(between, '50')).toBeNull()
  })
})
