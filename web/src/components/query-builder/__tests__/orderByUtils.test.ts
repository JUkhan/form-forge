import { describe, it, expect } from 'vitest'
import {
  addOrderByClause,
  removeOrderByClause,
  moveOrderByClause,
  updateOrderByClause,
} from '../OrderByPanel'
import type { OrderByClause } from '../../../features/datasets/types/builderState'

function makeClause(
  table: string,
  col: string,
  dir: 'ASC' | 'DESC' = 'ASC',
): OrderByClause {
  return { tableName: table, columnName: col, direction: dir }
}

// ---------------------------------------------------------------------------
// addOrderByClause
// ---------------------------------------------------------------------------

describe('addOrderByClause', () => {
  it('appends a clause with correct table/column/direction to an empty array', () => {
    const result = addOrderByClause([], 'users', 'created_at')
    expect(result).toHaveLength(1)
    expect(result[0]).toEqual({
      tableName: 'users',
      columnName: 'created_at',
      direction: 'ASC',
    })
  })

  it('appends to a non-empty array without mutating the original', () => {
    const original = [makeClause('users', 'id')]
    const result = addOrderByClause(original, 'orders', 'total')
    expect(result).toHaveLength(2)
    expect(result[1]).toEqual({ tableName: 'orders', columnName: 'total', direction: 'ASC' })
    // original is untouched (immutability)
    expect(original).toHaveLength(1)
    expect(result).not.toBe(original)
  })
})

// ---------------------------------------------------------------------------
// removeOrderByClause
// ---------------------------------------------------------------------------

describe('removeOrderByClause', () => {
  it('removes the clause at index 0 from a 3-element array', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2'), makeClause('c', 'c3')]
    const result = removeOrderByClause(clauses, 0)
    expect(result).toHaveLength(2)
    expect(result[0].tableName).toBe('b')
    expect(result[1].tableName).toBe('c')
  })

  it('removes the clause at the last index', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2'), makeClause('c', 'c3')]
    const result = removeOrderByClause(clauses, 2)
    expect(result).toHaveLength(2)
    expect(result[0].tableName).toBe('a')
    expect(result[1].tableName).toBe('b')
  })

  it('removes the clause at a middle index leaving surrounding clauses intact', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2'), makeClause('c', 'c3')]
    const result = removeOrderByClause(clauses, 1)
    expect(result).toHaveLength(2)
    expect(result[0].tableName).toBe('a')
    expect(result[1].tableName).toBe('c')
  })
})

// ---------------------------------------------------------------------------
// moveOrderByClause
// ---------------------------------------------------------------------------

describe('moveOrderByClause', () => {
  it('moves an item UP: element at index 1 moves to index 0, others preserved', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2'), makeClause('c', 'c3')]
    const result = moveOrderByClause(clauses, 1, 'up')
    expect(result[0].tableName).toBe('b')
    expect(result[1].tableName).toBe('a')
    expect(result[2].tableName).toBe('c')
  })

  it('moves an item DOWN: element at index 0 moves to index 1, others preserved', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2'), makeClause('c', 'c3')]
    const result = moveOrderByClause(clauses, 0, 'down')
    expect(result[0].tableName).toBe('b')
    expect(result[1].tableName).toBe('a')
    expect(result[2].tableName).toBe('c')
  })

  it('is a no-op at the upper boundary: moving index 0 up returns same content', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2')]
    const result = moveOrderByClause(clauses, 0, 'up')
    expect(result.map((c) => c.tableName)).toEqual(['a', 'b'])
  })

  it('is a no-op at the lower boundary: moving the last index down returns same content', () => {
    const clauses = [makeClause('a', 'c1'), makeClause('b', 'c2')]
    const result = moveOrderByClause(clauses, clauses.length - 1, 'down')
    expect(result.map((c) => c.tableName)).toEqual(['a', 'b'])
  })
})

// ---------------------------------------------------------------------------
// updateOrderByClause
// ---------------------------------------------------------------------------

describe('updateOrderByClause', () => {
  it('updates direction only, leaving other fields unchanged', () => {
    const clauses = [makeClause('users', 'created_at', 'ASC')]
    const result = updateOrderByClause(clauses, 0, { direction: 'DESC' })
    expect(result[0]).toEqual({
      tableName: 'users',
      columnName: 'created_at',
      direction: 'DESC',
    })
  })

  it('updates table and column together, leaving direction unchanged', () => {
    const clauses = [makeClause('users', 'created_at', 'DESC')]
    const result = updateOrderByClause(clauses, 0, { tableName: 'orders', columnName: 'total' })
    expect(result[0]).toEqual({
      tableName: 'orders',
      columnName: 'total',
      direction: 'DESC',
    })
  })

  it('returns an unchanged array when idx is out of range', () => {
    const clauses = [makeClause('users', 'id')]
    const result = updateOrderByClause(clauses, 5, { direction: 'DESC' })
    expect(result).toHaveLength(1)
    expect(result[0]).toEqual({ tableName: 'users', columnName: 'id', direction: 'ASC' })
  })
})
