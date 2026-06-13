import { describe, it, expect } from 'vitest'
import {
  treeSetCombinator,
  treeAddItem,
  treeRemoveItem,
  treeMoveItem,
  treeUpdateCondition,
} from '../FilterConditionsDialog'
import type { FilterCondition, FilterGroup } from '../../../features/datasets/types/builderState'

function makeCond(id: string): FilterCondition {
  return { id, kind: 'condition', tableName: 't', columnName: 'c', operator: '=', value: '' }
}

function makeGroup(
  id: string,
  combinator: 'AND' | 'OR' = 'AND',
  ...items: (FilterCondition | FilterGroup)[]
): FilterGroup {
  return { id, kind: 'group', combinator, items }
}

// ---------------------------------------------------------------------------
// treeSetCombinator
// ---------------------------------------------------------------------------

describe('treeSetCombinator', () => {
  it('sets combinator on the root group', () => {
    const root = makeGroup('root', 'AND', makeCond('c1'))
    const result = treeSetCombinator(root, 'root', 'OR')
    expect(result.combinator).toBe('OR')
    expect(result.items).toHaveLength(1)
  })

  it('sets combinator on a nested sub-group by id', () => {
    const sub = makeGroup('sub', 'AND', makeCond('c1'))
    const root = makeGroup('root', 'AND', sub)
    const result = treeSetCombinator(root, 'sub', 'OR')
    expect(result.combinator).toBe('AND')
    const resultSub = result.items[0] as FilterGroup
    expect(resultSub.combinator).toBe('OR')
  })

  it('leaves sibling items and conditions unchanged', () => {
    const sub = makeGroup('sub', 'AND')
    const cond = makeCond('c1')
    const root = makeGroup('root', 'AND', cond, sub)
    const result = treeSetCombinator(root, 'sub', 'OR')
    expect(result.items[0]).toEqual(cond)
    expect((result.items[1] as FilterGroup).combinator).toBe('OR')
  })
})

// ---------------------------------------------------------------------------
// treeAddItem
// ---------------------------------------------------------------------------

describe('treeAddItem', () => {
  it('appends a condition to the root group', () => {
    const root = makeGroup('root')
    const cond = makeCond('c1')
    const result = treeAddItem(root, 'root', cond)
    expect(result.items).toHaveLength(1)
    expect(result.items[0]).toEqual(cond)
  })

  it('appends a condition to a nested sub-group', () => {
    const sub = makeGroup('sub')
    const root = makeGroup('root', 'AND', sub)
    const cond = makeCond('c1')
    const result = treeAddItem(root, 'sub', cond)
    expect(result.items).toHaveLength(1)
    const resultSub = result.items[0] as FilterGroup
    expect(resultSub.items).toHaveLength(1)
    expect(resultSub.items[0]).toEqual(cond)
  })

  it('appends a sub-group to root', () => {
    const root = makeGroup('root')
    const sub = makeGroup('sub')
    const result = treeAddItem(root, 'root', sub)
    expect(result.items).toHaveLength(1)
    expect((result.items[0] as FilterGroup).id).toBe('sub')
  })

  it('returns an unchanged tree (re-cloned) when groupId is not found', () => {
    const root = makeGroup('root', 'AND', makeCond('c1'))
    const cond = makeCond('c2')
    const result = treeAddItem(root, 'nonexistent', cond)
    expect(result.items).toHaveLength(1)
    expect(result.items[0]).toEqual(makeCond('c1'))
  })
})

// ---------------------------------------------------------------------------
// treeRemoveItem
// ---------------------------------------------------------------------------

describe('treeRemoveItem', () => {
  it('removes a condition from root', () => {
    const cond = makeCond('c1')
    const root = makeGroup('root', 'AND', cond, makeCond('c2'))
    const result = treeRemoveItem(root, 'c1')
    expect(result.items).toHaveLength(1)
    expect((result.items[0] as FilterCondition).id).toBe('c2')
  })

  it('removes a condition from a nested sub-group', () => {
    const cond = makeCond('c1')
    const sub = makeGroup('sub', 'AND', cond)
    const root = makeGroup('root', 'AND', sub)
    const result = treeRemoveItem(root, 'c1')
    const resultSub = result.items[0] as FilterGroup
    expect(resultSub.items).toHaveLength(0)
  })

  it('removes a sub-group from root', () => {
    const sub = makeGroup('sub')
    const root = makeGroup('root', 'AND', sub, makeCond('c1'))
    const result = treeRemoveItem(root, 'sub')
    expect(result.items).toHaveLength(1)
    expect((result.items[0] as FilterCondition).id).toBe('c1')
  })

  it('leaves tree unchanged when id not found', () => {
    const root = makeGroup('root', 'AND', makeCond('c1'))
    const result = treeRemoveItem(root, 'nonexistent')
    expect(result.items).toHaveLength(1)
  })
})

// ---------------------------------------------------------------------------
// treeMoveItem
// ---------------------------------------------------------------------------

describe('treeMoveItem', () => {
  it('moves an item up', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const root = makeGroup('root', 'AND', c1, c2)
    const result = treeMoveItem(root, 'c2', 'up')
    expect((result.items[0] as FilterCondition).id).toBe('c2')
    expect((result.items[1] as FilterCondition).id).toBe('c1')
  })

  it('moves an item down', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const root = makeGroup('root', 'AND', c1, c2)
    const result = treeMoveItem(root, 'c1', 'down')
    expect((result.items[0] as FilterCondition).id).toBe('c2')
    expect((result.items[1] as FilterCondition).id).toBe('c1')
  })

  it('is a no-op when moving the first item up', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const root = makeGroup('root', 'AND', c1, c2)
    const result = treeMoveItem(root, 'c1', 'up')
    expect((result.items[0] as FilterCondition).id).toBe('c1')
    expect((result.items[1] as FilterCondition).id).toBe('c2')
  })

  it('is a no-op when moving the last item down', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const root = makeGroup('root', 'AND', c1, c2)
    const result = treeMoveItem(root, 'c2', 'down')
    expect((result.items[0] as FilterCondition).id).toBe('c1')
    expect((result.items[1] as FilterCondition).id).toBe('c2')
  })

  it('moves an item inside a nested sub-group', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const sub = makeGroup('sub', 'AND', c1, c2)
    const root = makeGroup('root', 'AND', sub)
    const result = treeMoveItem(root, 'c2', 'up')
    const resultSub = result.items[0] as FilterGroup
    expect((resultSub.items[0] as FilterCondition).id).toBe('c2')
    expect((resultSub.items[1] as FilterCondition).id).toBe('c1')
  })
})

// ---------------------------------------------------------------------------
// treeUpdateCondition
// ---------------------------------------------------------------------------

describe('treeUpdateCondition', () => {
  it('replaces a condition in the root group', () => {
    const orig = makeCond('c1')
    const root = makeGroup('root', 'AND', orig, makeCond('c2'))
    const updated = { ...orig, columnName: 'updated_col' }
    const result = treeUpdateCondition(root, updated)
    const first = result.items[0] as FilterCondition
    expect(first.columnName).toBe('updated_col')
    expect((result.items[1] as FilterCondition).id).toBe('c2')
  })

  it('replaces a condition nested two levels deep', () => {
    const orig = makeCond('c1')
    const inner = makeGroup('inner', 'AND', orig)
    const sub = makeGroup('sub', 'AND', inner)
    const root = makeGroup('root', 'AND', sub)
    const updated = { ...orig, operator: '!=' as const }
    const result = treeUpdateCondition(root, updated)
    const resultSub = result.items[0] as FilterGroup
    const resultInner = resultSub.items[0] as FilterGroup
    const resultCond = resultInner.items[0] as FilterCondition
    expect(resultCond.operator).toBe('!=')
  })

  it('leaves unrelated conditions unchanged', () => {
    const c1 = makeCond('c1')
    const c2 = makeCond('c2')
    const root = makeGroup('root', 'AND', c1, c2)
    const updated = { ...c1, columnName: 'new_col' }
    const result = treeUpdateCondition(root, updated)
    expect((result.items[1] as FilterCondition).columnName).toBe('c')
  })
})

// ---------------------------------------------------------------------------
// Arbitrary depth — AC-4: no artificial depth limit
// ---------------------------------------------------------------------------

describe('arbitrary depth (AC-4)', () => {
  it('handles a 7-level-deep tree with no limit enforced', () => {
    // Build from deepest outward
    const deepest = makeGroup('g7')
    let tree: FilterGroup = makeGroup('g6', 'AND', deepest)
    tree = makeGroup('g5', 'AND', tree)
    tree = makeGroup('g4', 'AND', tree)
    tree = makeGroup('g3', 'AND', tree)
    tree = makeGroup('g2', 'AND', tree)
    const root = makeGroup('root', 'AND', tree)

    const newCond = makeCond('new-cond')
    const result = treeAddItem(root, 'g7', newCond)

    // Navigate the 7-level path
    const g2 = result.items[0] as FilterGroup
    const g3 = g2.items[0] as FilterGroup
    const g4 = g3.items[0] as FilterGroup
    const g5 = g4.items[0] as FilterGroup
    const g6 = g5.items[0] as FilterGroup
    const g7 = g6.items[0] as FilterGroup

    expect(g7.items).toHaveLength(1)
    expect(g7.items[0]).toEqual(newCond)
  })

  it('treeSetCombinator reaches a deeply nested group', () => {
    const g4 = makeGroup('g4', 'AND')
    const g3 = makeGroup('g3', 'AND', g4)
    const g2 = makeGroup('g2', 'AND', g3)
    const root = makeGroup('root', 'AND', g2)

    const result = treeSetCombinator(root, 'g4', 'OR')
    const rg2 = result.items[0] as FilterGroup
    const rg3 = rg2.items[0] as FilterGroup
    const rg4 = rg3.items[0] as FilterGroup
    expect(rg4.combinator).toBe('OR')
  })
})
