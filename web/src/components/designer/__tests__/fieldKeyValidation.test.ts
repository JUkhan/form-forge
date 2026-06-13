import { describe, it, expect } from 'vitest'
import { collectFieldKeyErrors, isValidFieldKey } from '../fieldKeyValidation'
import type { DesignerElement } from '@/types/designer'

describe('isValidFieldKey (AC-2 — AR-4 SQL-safe identifier regex)', () => {
  it.each([
    ['a', true, 'minimal valid'],
    ['_foo', true, 'starts with underscore'],
    ['abc_123', true, 'letters + digits + underscore'],
    ['_', true, 'single underscore'],
    ['a'.repeat(63), true, 'exactly 63 chars (PostgreSQL limit)'],
  ])('accepts %j (%s)', (input, expected) => {
    expect(isValidFieldKey(input)).toBe(expected)
  })

  it.each([
    ['', false, 'empty — required'],
    ['1abc', false, 'starts with digit'],
    ['Abc', false, 'uppercase letter'],
    ['with-hyphen', false, 'hyphen not allowed'],
    ['field key', false, 'space not allowed'],
    ['field.key', false, 'dot not allowed'],
    ['a'.repeat(64), false, 'exceeds 63 chars'],
  ])('rejects %j (%s)', (input, expected) => {
    expect(isValidFieldKey(input)).toBe(expected)
  })
})

describe('collectFieldKeyErrors (AC-2, AC-3 — Story 3.6 pre-save gate)', () => {
  function leaf(id: string, type: string, fieldKey?: string): DesignerElement {
    return {
      id,
      type,
      properties: fieldKey === undefined ? {} : { fieldKey },
      children: [],
    }
  }

  function stack(id: string, children: DesignerElement[]): DesignerElement {
    return { id, type: 'Stack', properties: {}, children }
  }

  it('returns no errors for a valid tree with distinct fieldKeys', () => {
    const tree = stack('root', [
      leaf('a', 'Text Input', 'full_name'),
      leaf('b', 'Number Input', 'age'),
      leaf('c', 'Checkbox', 'is_active'),
    ])
    expect(collectFieldKeyErrors(tree)).toEqual([])
  })

  it('flags an input-bearing component with no fieldKey property', () => {
    const tree = stack('root', [leaf('a', 'Text Input')])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('missing a field key')
    expect(errors[0]).toContain('Text Input')
  })

  it('flags an input-bearing component with an empty-string fieldKey', () => {
    const tree = stack('root', [leaf('a', 'Number Input', '')])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('missing a field key')
  })

  it('flags an invalid fieldKey (fails AR-4 regex)', () => {
    const tree = stack('root', [leaf('a', 'Text Input', 'My Field')])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('invalid field key')
    expect(errors[0]).toContain('"My Field"')
  })

  it('flags two components sharing the same fieldKey as a collision', () => {
    const tree = stack('root', [
      leaf('a', 'Text Input', 'shared'),
      leaf('b', 'Number Input', 'shared'),
    ])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('multiple components')
    expect(errors[0]).toContain('"shared"')
    expect(errors[0]).toContain('a')
    expect(errors[0]).toContain('b')
  })

  it.each([
    ['Stack'],
    ['Row'],
    ['Tabs'],
    ['Label'],
    ['Button'],
  ])('does not check %s for fieldKey (non-input-bearing)', (type) => {
    const tree: DesignerElement = {
      id: 'root',
      type: 'Stack',
      properties: {},
      children: [{ id: 'x', type, properties: {}, children: [] }],
    }
    expect(collectFieldKeyErrors(tree)).toEqual([])
  })

  it('walks into children recursively', () => {
    const tree = stack('root', [
      stack('row1', [
        stack('inner', [leaf('deep', 'Text Input')]),
      ]),
    ])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('Text Input')
  })

  it('returns multiple violations in one pass', () => {
    const tree = stack('root', [
      leaf('a', 'Text Input'), // missing
      leaf('b', 'Number Input', 'Bad Key'), // invalid
      leaf('c', 'Checkbox', 'shared'),
      leaf('d', 'Dropdown', 'shared'), // collision
    ])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(3)
  })

  it('requires a fieldKey on a Repeater container (names the one-to-many relation)', () => {
    const tree = stack('root', [
      { id: 'rep', type: 'Repeater', properties: {}, children: [] },
    ])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('Repeater')
  })

  it('does not require a fieldKey on a Repeater Field (it references the row form by fieldName)', () => {
    const tree = stack('root', [
      {
        id: 'rep',
        type: 'Repeater',
        properties: { fieldKey: 'items' }, // container key required + provided
        children: [leaf('rf', 'Repeater Field')], // child needs none
      },
    ])
    expect(collectFieldKeyErrors(tree)).toEqual([])
  })

  it('flags a whitespace-only fieldKey as "missing" (not as "invalid")', () => {
    const tree = stack('root', [leaf('a', 'Text Input', '   ')])
    const errors = collectFieldKeyErrors(tree)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('missing a field key')
    // Specifically NOT the "invalid field key" path — that would render an
    // empty-quotes message like `invalid field key: "   "` which is useless.
    expect(errors[0]).not.toContain('invalid field key')
  })

  it('does not stack-overflow on a pathologically deep children chain', () => {
    // Build a 200-deep chain of Stacks (well past MAX_DEPTH=64). Without the
    // depth guard the recursive walk would blow the JS engine stack and crash
    // the SPA on Save.
    let leafTree: DesignerElement = { id: 'leaf', type: 'Stack', properties: {}, children: [] }
    for (let i = 0; i < 200; i++) {
      leafTree = { id: `n${i}`, type: 'Stack', properties: {}, children: [leafTree] }
    }
    const errors = collectFieldKeyErrors(leafTree)
    expect(errors.some((e) => e.includes('maximum depth'))).toBe(true)
  })
})
