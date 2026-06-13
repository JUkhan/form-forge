import { describe, expect, it } from 'vitest'
import { extractColumns, buildDerivedAlias } from './useDesignerFieldKeys'
import type { DesignerElement } from '@/types/designer'

// Story B-1-4-followup — pure column-resolution logic for the record-list table:
// showInTable visibility, columnHeader override, columnOrder ordering, and the
// derived (subquery-backed) columns for Designer-backed Dropdown + Repeater.

function stack(children: DesignerElement[]): DesignerElement {
  return { id: 'root', type: 'Stack', properties: {}, children }
}

describe('extractColumns', () => {
  it('emits a sortable, filterable column per scalar field with humanized header', () => {
    const cols = extractColumns(
      stack([
        { id: 'a', type: 'Text Input', properties: { fieldKey: 'full_name' } },
        { id: 'b', type: 'Number Input', properties: { fieldKey: 'age' } },
      ]),
    )
    expect(cols).toEqual([
      { dataKey: 'full_name', header: 'Full Name', order: 0, sortable: true, filterKind: 'text' },
      { dataKey: 'age', header: 'Age', order: 1, sortable: true, filterKind: 'number' },
    ])
  })

  it('hides fields with showInTable === false', () => {
    const cols = extractColumns(
      stack([
        { id: 'a', type: 'Text Input', properties: { fieldKey: 'visible' } },
        { id: 'b', type: 'Text Input', properties: { fieldKey: 'hidden', showInTable: false } },
      ]),
    )
    expect(cols.map((c) => c.dataKey)).toEqual(['visible'])
  })

  it('honors columnHeader override and columnOrder reordering', () => {
    const cols = extractColumns(
      stack([
        { id: 'a', type: 'Text Input', properties: { fieldKey: 'first', columnOrder: 2 } },
        {
          id: 'b',
          type: 'Text Input',
          properties: { fieldKey: 'second', columnOrder: 1, columnHeader: 'Custom Header' },
        },
      ]),
    )
    expect(cols.map((c) => [c.dataKey, c.header])).toEqual([
      ['second', 'Custom Header'],
      ['first', 'First'],
    ])
  })

  it('emits a derived, display-only label column for a Designer-backed Dropdown', () => {
    const cols = extractColumns(
      stack([
        {
          id: 'a',
          type: 'Dropdown',
          properties: {
            fieldKey: 'factory',
            optionsSource: 'designer',
            optionsDesignerId: 'factory',
            labelField: 'name',
          },
        },
      ]),
    )
    const col = cols[0]
    expect(col.dataKey).toBe('factory_name')
    expect(col.sortable).toBe(false)
    expect(col.filterKind).toBeUndefined()
    // No version/valueField → no reference filter offered.
    expect(col.referenceFilter).toBeUndefined()
  })

  it('attaches a reference filter (combobox config) when version + valueField are set', () => {
    const cols = extractColumns(
      stack([
        {
          id: 'a',
          type: 'Dropdown',
          properties: {
            fieldKey: 'factory',
            optionsSource: 'designer',
            optionsDesignerId: 'factory',
            optionsVersion: 3,
            labelField: 'name',
            valueField: 'id',
          },
        },
      ]),
    )
    const col = cols[0]
    // Display alias stays the derived label; filter targets the FK column.
    expect(col.dataKey).toBe('factory_name')
    expect(col.referenceFilter).toEqual({
      filterColumn: 'factory',
      designerId: 'factory',
      version: 3,
      labelField: 'name',
      valueField: 'id',
    })
  })

  it('emits the raw column for a static Dropdown (sortable/filterable)', () => {
    const cols = extractColumns(
      stack([
        { id: 'a', type: 'Dropdown', properties: { fieldKey: 'status', optionsSource: 'static' } },
      ]),
    )
    expect(cols[0]).toMatchObject({ dataKey: 'status', sortable: true, filterKind: 'text' })
  })

  it('emits a derived, display-only row-count column for a Repeater', () => {
    const cols = extractColumns(
      stack([
        { id: 'a', type: 'Repeater', properties: { fieldKey: 'lines', rowDesignerId: 'line_item' } },
      ]),
    )
    expect(cols[0]).toMatchObject({
      dataKey: 'line_item_row_count',
      sortable: false,
    })
    expect(cols[0].filterKind).toBeUndefined()
  })
})

describe('buildDerivedAlias', () => {
  it('joins with an underscore', () => {
    expect(buildDerivedAlias('factory', 'name')).toBe('factory_name')
  })

  it('truncates to 63 characters to mirror Postgres NAMEDATALEN', () => {
    const alias = buildDerivedAlias('a'.repeat(60), 'row_count')
    expect(alias).toHaveLength(63)
    expect(alias).toBe(('a'.repeat(60) + '_row_count').slice(0, 63))
  })
})
