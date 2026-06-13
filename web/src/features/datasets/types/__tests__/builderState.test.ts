// Story 9.5 (FR-66 AC-3) — left-table validation gate. This is the single source
// of truth consumed by the canvas validation banner (Story 9.5) and, when Save/
// Preview land, by Stories 11.2/11.3 to disable those actions.
import { describe, expect, it } from 'vitest'
import {
  getColumnSelectionValidationError,
  getLeftTableValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
  isValidAlias,
  parseBuilderState,
  FILTER_OPERATORS,
  CASE_OPERATORS,
  EMPTY_BUILDER_STATE,
} from '../builderState'
import type {
  BuilderState,
  CalculatedColumn,
  CaseColumn,
  ColumnSelection,
  FilterCondition,
  FilterGroup,
  TableNodeState,
  TableSide,
} from '../builderState'

// Minimal node shape — the helper only reads `data.side`.
function node(side: TableSide): Pick<TableNodeState, 'data'> {
  return { data: { tableName: 't', side, columns: [] } }
}

// Minimal CaseColumn shape for the case-column alias gate tests.
function caseCol(alias: string): CaseColumn {
  return {
    id: 'c1',
    nodeId: 'n1',
    alias,
    whens: [{ nodeId: 'n1', columnName: 'id', operator: '=', operandValue: '1', thenValue: 'a' }],
    elseValue: '',
  }
}

// Minimal CalculatedColumn shape for the calculated-column alias gate tests.
function calcCol(alias: string): CalculatedColumn {
  return { id: 'cc1', nodeId: 'n1', alias, expression: 'price * qty' }
}

// Minimal column shape for the column-selection gate tests.
function col(checked: boolean): ColumnSelection {
  return { columnName: 'id', pgType: 'uuid', checked, aggregate: 'none', alias: '' }
}

// Minimal node carrying columns — the column-selection helper reads `data.columns`.
function nodeWithColumns(columns: ColumnSelection[]): Pick<TableNodeState, 'data'> {
  return { data: { tableName: 't', side: 'left', columns } }
}

describe('getLeftTableValidationError', () => {
  it('returns null for an empty canvas (nothing to query yet)', () => {
    expect(getLeftTableValidationError([])).toBeNull()
  })

  it('returns null for a single left table', () => {
    expect(getLeftTableValidationError([node('left')])).toBeNull()
  })

  it('returns null for a single right table (a lone table is its own FROM anchor)', () => {
    expect(getLeftTableValidationError([node('right')])).toBeNull()
  })

  it('returns null when at least one of several tables is left', () => {
    expect(getLeftTableValidationError([node('left'), node('right')])).toBeNull()
  })

  it('returns the i18n key when more than one table and none is left', () => {
    expect(getLeftTableValidationError([node('right'), node('right')])).toBe(
      'datasets.builder.validation.noLeftTable',
    )
  })

  it('returns null when multiple tables are left (read-time gate only checks "has a left")', () => {
    // The single-Left invariant is enforced at write time in handleSideChange,
    // not by this read-time validity check.
    expect(getLeftTableValidationError([node('left'), node('left')])).toBeNull()
  })
})

describe('getColumnSelectionValidationError', () => {
  it('returns null for an empty canvas (nothing to validate)', () => {
    expect(getColumnSelectionValidationError([])).toBeNull()
  })

  it('returns the i18n key for a single node with no checked columns (degenerate 0-column node still has nothing selected)', () => {
    // The gate flags any node-set with zero checked columns; a 0-column node
    // (never produced by the catalog in practice) has nothing selected, so it
    // is treated as invalid — a SELECT with no columns is not a valid query.
    expect(getColumnSelectionValidationError([nodeWithColumns([])])).toBe(
      'datasets.builder.validation.noColumnsSelected',
    )
  })

  it('returns the i18n key for a single node with one unchecked column', () => {
    expect(getColumnSelectionValidationError([nodeWithColumns([col(false)])])).toBe(
      'datasets.builder.validation.noColumnsSelected',
    )
  })

  it('returns null for a single node with one checked column', () => {
    expect(getColumnSelectionValidationError([nodeWithColumns([col(true)])])).toBeNull()
  })

  it('returns the i18n key when all columns across multiple nodes are unchecked', () => {
    expect(
      getColumnSelectionValidationError([
        nodeWithColumns([col(false), col(false)]),
        nodeWithColumns([col(false)]),
      ]),
    ).toBe('datasets.builder.validation.noColumnsSelected')
  })

  it('returns null when any column on any node is checked', () => {
    expect(
      getColumnSelectionValidationError([
        nodeWithColumns([col(false)]),
        nodeWithColumns([col(false), col(true)]),
      ]),
    ).toBeNull()
  })
})

describe('isValidAlias', () => {
  it('returns true for an empty string (use-default signal)', () => {
    expect(isValidAlias('')).toBe(true)
  })

  it('returns true for a valid lowercase alias', () => {
    expect(isValidAlias('my_alias')).toBe(true)
  })

  it('returns true for an alias starting with underscore', () => {
    expect(isValidAlias('_private')).toBe(true)
  })

  it('returns true for an alias ending with digit', () => {
    expect(isValidAlias('col1')).toBe(true)
  })

  it('returns false for uppercase letters', () => {
    expect(isValidAlias('MyAlias')).toBe(false)
  })

  it('returns false for leading digit', () => {
    expect(isValidAlias('1alias')).toBe(false)
  })

  it('returns false for spaces', () => {
    expect(isValidAlias('alias name')).toBe(false)
  })

  it('returns false for hyphens', () => {
    expect(isValidAlias('alias-col')).toBe(false)
  })

  it('returns true for exactly 63 chars', () => {
    expect(isValidAlias('a'.repeat(63))).toBe(true)
  })

  it('returns false for 64 chars (exceeds PostgreSQL identifier limit)', () => {
    expect(isValidAlias('a'.repeat(64))).toBe(false)
  })
})

describe('getCaseColumnAliasError', () => {
  it('returns null for an empty caseColumns array (nothing to validate)', () => {
    expect(getCaseColumnAliasError([])).toBeNull()
  })

  it('returns null when all case columns have non-empty aliases', () => {
    expect(getCaseColumnAliasError([caseCol('price_tier'), caseCol('status_label')])).toBeNull()
  })

  it('returns the i18n key when one case column has an empty alias', () => {
    expect(getCaseColumnAliasError([caseCol('price_tier'), caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns the i18n key when all case columns have empty aliases', () => {
    expect(getCaseColumnAliasError([caseCol(''), caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns the i18n key for a single case column with an empty alias', () => {
    expect(getCaseColumnAliasError([caseCol('')])).toBe(
      'datasets.builder.validation.caseColumnRequiresAlias',
    )
  })

  it('returns null for a single case column with a valid alias', () => {
    expect(getCaseColumnAliasError([caseCol('my_case')])).toBeNull()
  })
})

describe('FILTER_OPERATORS', () => {
  it('contains all 13 expected operators', () => {
    const expected = [
      '=', '!=', '<', '<=', '>', '>=',
      'IS NULL', 'IS NOT NULL', 'LIKE', 'ILIKE', 'IN', 'NOT IN', 'BETWEEN',
    ]
    expect(FILTER_OPERATORS).toHaveLength(13)
    expected.forEach((op) => expect(FILTER_OPERATORS).toContain(op))
  })

  it('equals CASE_OPERATORS exactly (same vocabulary per spec FR-68 J-3 AC-2)', () => {
    expect(FILTER_OPERATORS).toEqual(CASE_OPERATORS)
  })
})

describe('EMPTY_BUILDER_STATE.filters', () => {
  it('is a root FilterGroup with id root, kind group, AND combinator, and empty items', () => {
    const filters: FilterGroup = EMPTY_BUILDER_STATE.filters
    expect(filters.id).toBe('root')
    expect(filters.kind).toBe('group')
    expect(filters.combinator).toBe('AND')
    expect(filters.items).toEqual([])
  })
})

describe('getCalculatedColumnAliasError', () => {
  it('returns null for an empty calculatedColumns array (nothing to validate)', () => {
    expect(getCalculatedColumnAliasError([])).toBeNull()
  })

  it('returns null when all calculated columns have non-empty aliases', () => {
    expect(
      getCalculatedColumnAliasError([calcCol('total_price'), calcCol('discount_amount')]),
    ).toBeNull()
  })

  it('returns the i18n key when one calculated column has an empty alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('total_price'), calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns the i18n key when all calculated columns have empty aliases', () => {
    expect(getCalculatedColumnAliasError([calcCol(''), calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns the i18n key for a single calculated column with an empty alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('')])).toBe(
      'datasets.builder.validation.calculatedColumnRequiresAlias',
    )
  })

  it('returns null for a single calculated column with a valid alias', () => {
    expect(getCalculatedColumnAliasError([calcCol('my_calc')])).toBeNull()
  })
})

// Story 10.6 (FR-68 J-6 AC-3/AC-4): FilterCondition.value is `string | string[] | null`.
// These assert the value shape survives a parseBuilderState round-trip (the JSON
// blob persisted in custom_dataset.builder_state), which is what Story 11.1's SQL
// generator reads to choose between a scalar placeholder, IN (…), and BETWEEN.
describe('FilterCondition value type', () => {
  function roundTripCondition(value: string | string[] | null): FilterCondition {
    const filters: FilterGroup = {
      id: 'root',
      kind: 'group',
      combinator: 'AND',
      items: [
        {
          id: 'cond-1',
          kind: 'condition',
          tableName: 'orders',
          columnName: 'total',
          operator: '=',
          value,
        },
      ],
    }
    const state: BuilderState = { ...EMPTY_BUILDER_STATE, filters }
    const parsed = parseBuilderState(JSON.stringify(state))
    return parsed.filters.items[0] as FilterCondition
  }

  it('single value stored as string', () => {
    const cond = roundTripCondition('42')
    expect(typeof cond.value).toBe('string')
    expect(cond.value).toBe('42')
  })

  it('IN operator stored as string[]', () => {
    const cond = roundTripCondition(['a', 'b', 'c'])
    expect(Array.isArray(cond.value)).toBe(true)
    expect(cond.value).toEqual(['a', 'b', 'c'])
  })

  it('BETWEEN operator stored as string[] with two elements', () => {
    const cond = roundTripCondition(['1', '10'])
    expect(Array.isArray(cond.value)).toBe(true)
    expect((cond.value as string[]).length).toBe(2)
    expect(cond.value).toEqual(['1', '10'])
  })

  it('IS NULL operator stored as null', () => {
    const cond = roundTripCondition(null)
    expect(cond.value).toBeNull()
  })
})

// Story 11.2 (FR-71 AC-2/AC-3): parseBuilderState is a pure, never-throwing function
// that normalizes every field of a persisted builder_state blob. Missing or malformed
// fields fall back to safe defaults; malformed array entries are dropped. These guard
// the restore path against schema drift and hand-crafted/corrupt blobs.
describe('parseBuilderState normalization', () => {
  // A fully-populated, valid state used by round-trip / preservation tests.
  function validState(): BuilderState {
    const columns: ColumnSelection[] = [
      { columnName: 'id', pgType: 'integer', checked: true, aggregate: 'none', alias: 'order_id' },
      { columnName: 'amount', pgType: 'numeric', checked: true, aggregate: 'SUM', alias: 'total' },
    ]
    const nodes: TableNodeState[] = [
      { id: 'n1', type: 'tableNode', position: { x: 10, y: 20 }, data: { tableName: 'orders', side: 'left', columns } },
      { id: 'n2', type: 'tableNode', position: { x: 300, y: 40 }, data: { tableName: 'items', side: 'right', columns: [{ columnName: 'order_id', pgType: 'integer', checked: false, aggregate: 'none', alias: '' }] } },
    ]
    const edges = [
      {
        id: 'e1', source: 'n1', target: 'n2', sourceHandle: 'id', targetHandle: 'order_id',
        type: 'joinEdge' as const,
        data: { sourceColumn: 'id', targetColumn: 'order_id', joinType: 'LEFT' as const },
      },
    ]
    const filters: FilterGroup = {
      id: 'root', kind: 'group', combinator: 'AND',
      items: [
        { id: 'cond-1', kind: 'condition', tableName: 'orders', columnName: 'amount', operator: '>', value: '100' },
      ],
    }
    const caseColumns: CaseColumn[] = [
      { id: 'case-n1-1', nodeId: 'n1', alias: 'tier', whens: [{ nodeId: 'n1', columnName: 'amount', operator: '>', operandValue: '50', thenValue: 'high' }], elseValue: 'low' },
    ]
    const calculatedColumns: CalculatedColumn[] = [
      { id: 'calc-n1-1', nodeId: 'n1', alias: 'doubled', expression: 'amount * 2' },
    ]
    return {
      nodes,
      edges,
      filters,
      orderBy: [{ tableName: 'orders', columnName: 'id', direction: 'DESC' }],
      caseColumns,
      calculatedColumns,
    }
  }

  it('returns EMPTY_BUILDER_STATE for null input', () => {
    expect(parseBuilderState(null)).toEqual(EMPTY_BUILDER_STATE)
  })

  it('returns EMPTY_BUILDER_STATE for empty string', () => {
    expect(parseBuilderState('')).toEqual(EMPTY_BUILDER_STATE)
  })

  it('returns EMPTY_BUILDER_STATE for invalid JSON', () => {
    expect(parseBuilderState('{not valid json')).toEqual(EMPTY_BUILDER_STATE)
  })

  it('returns EMPTY_BUILDER_STATE for a non-object JSON value (array at root)', () => {
    expect(parseBuilderState('[1, 2, 3]')).toEqual(EMPTY_BUILDER_STATE)
  })

  it('normalizes a blob missing orderBy to empty array', () => {
    const blob = JSON.stringify({ nodes: [], edges: [], filters: EMPTY_BUILDER_STATE.filters, caseColumns: [], calculatedColumns: [] })
    expect(parseBuilderState(blob).orderBy).toEqual([])
  })

  it('normalizes a blob missing caseColumns to empty array', () => {
    const blob = JSON.stringify({ nodes: [], edges: [], filters: EMPTY_BUILDER_STATE.filters, orderBy: [], calculatedColumns: [] })
    expect(parseBuilderState(blob).caseColumns).toEqual([])
  })

  it('normalizes a blob missing calculatedColumns to empty array', () => {
    const blob = JSON.stringify({ nodes: [], edges: [], filters: EMPTY_BUILDER_STATE.filters, orderBy: [], caseColumns: [] })
    expect(parseBuilderState(blob).calculatedColumns).toEqual([])
  })

  it('normalizes a blob missing filters to EMPTY_BUILDER_STATE.filters', () => {
    const blob = JSON.stringify({ nodes: [], edges: [], orderBy: [], caseColumns: [], calculatedColumns: [] })
    expect(parseBuilderState(blob).filters).toEqual(EMPTY_BUILDER_STATE.filters)
  })

  it('normalizes a blob where filters is wrong shape (not a group) to EMPTY_BUILDER_STATE.filters', () => {
    const blob = JSON.stringify({ nodes: [], edges: [], filters: { foo: 'bar' }, orderBy: [], caseColumns: [], calculatedColumns: [] })
    expect(parseBuilderState(blob).filters).toEqual(EMPTY_BUILDER_STATE.filters)
  })

  it("normalizes per-column fields: missing checked → false, missing aggregate → 'none', missing alias → ''", () => {
    const blob = JSON.stringify({
      nodes: [{ id: 'n1', type: 'tableNode', position: { x: 0, y: 0 }, data: { tableName: 'orders', side: 'left', columns: [{ columnName: 'id', pgType: 'integer' }] } }],
      edges: [],
    })
    const col = parseBuilderState(blob).nodes[0].data.columns[0]
    expect(col.checked).toBe(false)
    expect(col.aggregate).toBe('none')
    expect(col.alias).toBe('')
  })

  it("normalizes edge data: missing joinType → 'INNER'", () => {
    const blob = JSON.stringify({
      nodes: [],
      edges: [{ id: 'e1', source: 'n1', target: 'n2', sourceHandle: 'id', targetHandle: 'order_id', type: 'joinEdge', data: { sourceColumn: 'id', targetColumn: 'order_id' } }],
    })
    expect(parseBuilderState(blob).edges[0].data.joinType).toBe('INNER')
  })

  it('preserves valid nodes, edges, filters, orderBy, caseColumns, calculatedColumns on a round-trip', () => {
    const state = validState()
    expect(parseBuilderState(JSON.stringify(state))).toEqual(state)
  })

  it('round-trips EMPTY_BUILDER_STATE unchanged', () => {
    expect(parseBuilderState(JSON.stringify(EMPTY_BUILDER_STATE))).toEqual(EMPTY_BUILDER_STATE)
  })

  it('normalizes nested filter group items (recursive)', () => {
    const blob = JSON.stringify({
      nodes: [],
      edges: [],
      filters: {
        id: 'root', kind: 'group', combinator: 'OR',
        items: [
          {
            id: 'grp-1', kind: 'group', combinator: 'AND',
            items: [
              { id: 'cond-1', kind: 'condition', tableName: 'orders', columnName: 'id', operator: '=', value: '1' },
            ],
          },
          { id: 'cond-2', kind: 'condition', tableName: 'orders', columnName: 'status', operator: 'IS NULL', value: null },
        ],
      },
    })
    const filters = parseBuilderState(blob).filters
    expect(filters.combinator).toBe('OR')
    expect(filters.items).toHaveLength(2)
    const nested = filters.items[0] as FilterGroup
    expect(nested.kind).toBe('group')
    expect(nested.items).toHaveLength(1)
    expect((nested.items[0] as FilterCondition).columnName).toBe('id')
  })

  it('drops malformed nodes (non-object entries in the nodes array)', () => {
    const blob = JSON.stringify({
      nodes: [
        null,
        42,
        { id: 'n1', type: 'tableNode', position: { x: 0, y: 0 }, data: { tableName: 'orders', side: 'left', columns: [] } },
      ],
      edges: [],
    })
    const nodes = parseBuilderState(blob).nodes
    expect(nodes).toHaveLength(1)
    expect(nodes[0].id).toBe('n1')
  })
})
