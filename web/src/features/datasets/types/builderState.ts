// AR-67: Canonical BuilderState interface. This file is the single source of truth
// for the builder_state JSON schema stored in custom_dataset.builder_state (JSONB).
// C# BuilderStateDto mirrors this exactly (Decision 6.11).
// Epic 10 adds column selection, aggregates, filters, ORDER BY, CASE, and calculated
// columns — extend only here and in the C# mirror together.

export type JoinType = 'INNER' | 'LEFT' | 'RIGHT' | 'FULL OUTER'
export type TableSide = 'left' | 'right'
export type AggregateFunction = 'none' | 'COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX'

// Operator vocabulary for CASE WHEN conditions — same set as Filter Conditions (Story 10.6
// reuses this type). Defined here first (Story 10.3); Story 10.6 confirms the canonical form.
export type CaseOperator =
  | '=' | '!=' | '<' | '<=' | '>' | '>='
  | 'IS NULL' | 'IS NOT NULL' | 'LIKE' | 'ILIKE'
  | 'IN' | 'NOT IN' | 'BETWEEN'

export const CASE_OPERATORS: CaseOperator[] = [
  '=', '!=', '<', '<=', '>', '>=',
  'IS NULL', 'IS NOT NULL', 'LIKE', 'ILIKE', 'IN', 'NOT IN', 'BETWEEN',
]

// One WHEN arm: column condition + THEN value.
// operandValue: raw string RHS; ignored for IS NULL / IS NOT NULL.
// thenValue: raw string; Story 11.1's SQL generator decides quoting/casting.
// Column references (AC-3) can be typed by name; Story 11.1 resolves them.
export interface CaseWhen {
  nodeId: string         // table node whose column is tested in the WHEN condition
  columnName: string     // column from that node
  operator: CaseOperator
  operandValue: string   // RHS value; empty for IS NULL / IS NOT NULL
  thenValue: string      // THEN output value
}

// Per-column state on a TableNode (Epic 10 fills in checked/aggregate/alias).
// `type` (not `interface`) so it satisfies React Flow v12's
// `Node<NodeData extends Record<string, unknown>>` constraint when nested in TableNodeData.
export type ColumnSelection = {
  columnName: string
  pgType: string
  checked: boolean       // Epic 10: Story 10.1
  aggregate: AggregateFunction  // Epic 10: Story 10.2
  alias: string          // Epic 10: Story 10.2
}

// `type` (not `interface`) so it satisfies React Flow v12's
// `Node<NodeData extends Record<string, unknown>>` constraint — TypeScript infers an
// implicit index signature for type-alias object types but not for interfaces.
export type TableNodeData = {
  tableName: string
  side: TableSide        // Story 9.5: designation control
  columns: ColumnSelection[]
}

// React Flow node shape for a table
export interface TableNodeState {
  id: string
  type: 'tableNode'
  position: { x: number; y: number }
  data: TableNodeData
}

// `type` (not `interface`) so it satisfies React Flow v12's
// `Edge<EdgeData extends Record<string, unknown>>` constraint (see TableNodeData note).
export type JoinEdgeData = {
  sourceColumn: string   // column name on source node
  targetColumn: string   // column name on target node
  joinType: JoinType     // Story 9.4: defaults to INNER
}

// React Flow edge shape for a join
export interface JoinEdgeState {
  id: string
  source: string         // node id
  target: string         // node id
  sourceHandle: string   // column name (Story 9.3)
  targetHandle: string   // column name (Story 9.3)
  type: 'joinEdge'
  data: JoinEdgeData
}

// Story 10.5: Expanded from Epic 10 stub.
// Uses `type` (not `interface`) to match the existing codebase pattern for domain
// types (ColumnSelection, TableNodeData, JoinEdgeData all use `type`).
// id: client-side stable identifier for React keys + update/delete targeting.
// kind: 'condition' discriminates from FilterGroup in the FilterItem union —
//   enables exhaustive narrowing in the dialog render layer.
// operator: FilterOperator = CaseOperator per spec (same vocabulary, FR-68 J-3 AC-2).
// value (Story 10.6):
//   - `string`   for single-value operators (=, !=, <, <=, >, >=, LIKE, ILIKE).
//   - `string[]` for IN / NOT IN (array of tag values) and for BETWEEN
//     (two-element [from, to]).
//   - `null`     for IS NULL / IS NOT NULL (no RHS).
//   Story 11.1's SQL generator reads `Array.isArray(value)` to decide between
//   IN ($1, $2, …) and BETWEEN $1 AND $2; values are always parameterized.
export type FilterCondition = {
  id: string
  kind: 'condition'
  tableName: string
  columnName: string
  operator: FilterOperator
  value: string | string[] | null
}

// Story 10.5: Expanded from Epic 10 stub.
// id: 'root' for the top-level group (convention); nextFilterId('grp') for sub-groups.
// kind: 'group' discriminates from FilterCondition in the FilterItem union.
// items: single ordered array of conditions and sub-groups, replacing the Epic 10
//   stub's separate conditions[]/groups[] arrays. A unified list enables
//   interleaved up/down reorder across types (AC-5). Recursive by referencing
//   (FilterCondition | FilterGroup)[] inline — TypeScript resolves this correctly
//   for structural type aliases through an array member.
export type FilterGroup = {
  id: string
  kind: 'group'
  combinator: 'AND' | 'OR'
  items: (FilterCondition | FilterGroup)[]
}

// Convenience union alias for the dialog render layer.
export type FilterItem = FilterCondition | FilterGroup

// FilterOperator is identical to CaseOperator — the spec reuses the same operator
// vocabulary for both filter conditions and CASE WHEN arms (FR-68 J-3 AC-2).
// Defined as a type alias so CaseWhen.operator typings in CaseColumnEditor.tsx
// are undisturbed — no renames needed.
export type FilterOperator = CaseOperator

// SSOT for the operator vocabulary consumed by FilterConditionRow (Story 10.6) and
// by CaseColumnEditor (Story 10.3). Story 10.6 imports FILTER_OPERATORS for its
// operator <select> — do NOT use CASE_OPERATORS directly in new filter code.
export const FILTER_OPERATORS: FilterOperator[] = [...CASE_OPERATORS]

export interface OrderByClause {
  tableName: string
  columnName: string
  direction: 'ASC' | 'DESC'
}

// Story 10.3: expanded from the Epic 10 stub.
// id: client-side stable identifier for React keys + update/delete targeting.
// nodeId: table node this CASE column is attached to (display grouping; WHEN arms
//   may reference columns from any node on the canvas per AC-3).
// alias: required (non-empty) — getCaseColumnAliasError gates Save until set (AC-4).
// elseValue: '' → no ELSE clause; any string → ELSE <elseValue> in SQL (Story 11.1).
export interface CaseColumn {
  id: string
  nodeId: string
  alias: string
  whens: CaseWhen[]    // ≥1 arms; UI ensures minimum 1 on creation
  elseValue: string    // empty string = no ELSE
}

// Story 10.4: expanded from the Epic 10 stub.
// id: client-side stable identifier for React keys + update/delete targeting.
// nodeId: table node this calculated column is attached to (display grouping;
//   expression may reference columns from any table per SQL conventions).
// alias: required (non-empty) — getCalculatedColumnAliasError gates Save (AC-3).
// expression: free-form SQL expression string; ExpressionSecurityValidator.cs
//   validates on save (Story 11.1). Do NOT pre-validate here — security is server-side.
export interface CalculatedColumn {
  id: string
  nodeId: string
  alias: string
  expression: string
}

export interface BuilderState {
  nodes: TableNodeState[]
  edges: JoinEdgeState[]
  filters: FilterGroup
  orderBy: OrderByClause[]
  caseColumns: CaseColumn[]
  calculatedColumns: CalculatedColumn[]
}

export const EMPTY_BUILDER_STATE: BuilderState = {
  nodes: [],
  edges: [],
  // Story 10.5: root FilterGroup — id 'root' is a convention; non-root sub-groups
  // get nextFilterId('grp') ids from the FilterConditionsDialog.
  filters: { id: 'root', kind: 'group', combinator: 'AND', items: [] },
  orderBy: [],
  caseColumns: [],
  calculatedColumns: [],
}

// Parse builder_state from the JSON string stored in DatasetDetail.builderState.
// Returns EMPTY_BUILDER_STATE on null/corrupt/structurally-wrong data so the canvas
// never crashes on a bad blob (e.g. schema change between write and read).
//
// Story 11.2 (FR-71 AC-2/AC-3): a field-by-field normalization pass. Every array
// field that is missing or has the wrong shape falls back to a safe default, and every
// malformed entry inside an array is dropped (flatMap → []). This is a PURE function —
// it never throws. A persisted blob from an older schema version (pre-Epic 10 fields)
// still produces a usable (possibly empty) canvas rather than crashing.
export function parseBuilderState(raw: string | null): BuilderState {
  if (!raw) return EMPTY_BUILDER_STATE
  try {
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return EMPTY_BUILDER_STATE
    const p = parsed as Record<string, unknown>

    // nodes: normalize position + data.columns per-field defaults
    const nodes: TableNodeState[] = Array.isArray(p.nodes)
      ? (p.nodes as unknown[]).flatMap((n): TableNodeState[] => {
          if (typeof n !== 'object' || n === null) return []
          const nn = n as Record<string, unknown>
          const nodeId = typeof nn.id === 'string' ? nn.id : ''
          if (!nodeId) return []
          const data = (typeof nn.data === 'object' && nn.data !== null)
            ? (nn.data as Record<string, unknown>) : {}
          const columns: ColumnSelection[] = Array.isArray(data.columns)
            ? (data.columns as unknown[]).flatMap((c): ColumnSelection[] => {
                if (typeof c !== 'object' || c === null) return []
                const cc = c as Record<string, unknown>
                return [{
                  columnName: typeof cc.columnName === 'string' ? cc.columnName : '',
                  pgType: typeof cc.pgType === 'string' ? cc.pgType : 'text',
                  checked: cc.checked === true,
                  aggregate: (cc.aggregate as AggregateFunction | undefined) ?? 'none',
                  alias: typeof cc.alias === 'string' ? cc.alias : '',
                }]
              })
            : []
          return [{
            id: nodeId,
            type: 'tableNode' as const,
            position: {
              x: typeof (nn.position as Record<string, unknown>)?.x === 'number'
                ? (nn.position as Record<string, unknown>).x as number : 0,
              y: typeof (nn.position as Record<string, unknown>)?.y === 'number'
                ? (nn.position as Record<string, unknown>).y as number : 0,
            },
            data: {
              tableName: typeof data.tableName === 'string' ? data.tableName : '',
              side: (data.side === 'left' || data.side === 'right') ? data.side : 'right',
              columns,
            },
          }]
        })
      : []

    // edges: normalize required fields
    const edges: JoinEdgeState[] = Array.isArray(p.edges)
      ? (p.edges as unknown[]).flatMap((e): JoinEdgeState[] => {
          if (typeof e !== 'object' || e === null) return []
          const ee = e as Record<string, unknown>
          const edgeId = typeof ee.id === 'string' ? ee.id : ''
          const edgeSource = typeof ee.source === 'string' ? ee.source : ''
          const edgeTarget = typeof ee.target === 'string' ? ee.target : ''
          if (!edgeId || !edgeSource || !edgeTarget) return []
          const edgeData = (typeof ee.data === 'object' && ee.data !== null)
            ? (ee.data as Record<string, unknown>) : {}
          return [{
            id: edgeId,
            source: edgeSource,
            target: edgeTarget,
            sourceHandle: typeof ee.sourceHandle === 'string' ? ee.sourceHandle : '',
            targetHandle: typeof ee.targetHandle === 'string' ? ee.targetHandle : '',
            type: 'joinEdge' as const,
            data: {
              sourceColumn: typeof edgeData.sourceColumn === 'string' ? edgeData.sourceColumn : '',
              targetColumn: typeof edgeData.targetColumn === 'string' ? edgeData.targetColumn : '',
              joinType: (['INNER', 'LEFT', 'RIGHT', 'FULL OUTER'] as JoinType[])
                .includes(edgeData.joinType as JoinType)
                ? (edgeData.joinType as JoinType) : 'INNER',
            },
          }]
        })
      : []

    // filters: normalize the root FilterGroup
    const rawFilters = p.filters
    const filters: FilterGroup =
      typeof rawFilters === 'object' && rawFilters !== null &&
      (rawFilters as Record<string, unknown>).kind === 'group'
        ? normalizeFilterGroup(rawFilters as Record<string, unknown>)
        : EMPTY_BUILDER_STATE.filters

    // orderBy, caseColumns, calculatedColumns: normalize arrays
    const orderBy: OrderByClause[] = Array.isArray(p.orderBy)
      ? (p.orderBy as unknown[]).flatMap((o): OrderByClause[] => {
          if (typeof o !== 'object' || o === null) return []
          const oo = o as Record<string, unknown>
          return [{
            tableName: typeof oo.tableName === 'string' ? oo.tableName : '',
            columnName: typeof oo.columnName === 'string' ? oo.columnName : '',
            direction: (oo.direction === 'DESC') ? 'DESC' : 'ASC',
          }]
        })
      : []

    const caseColumns: CaseColumn[] = Array.isArray(p.caseColumns)
      ? (p.caseColumns as unknown[]).flatMap((c): CaseColumn[] => {
          if (typeof c !== 'object' || c === null) return []
          const cc = c as Record<string, unknown>
          const whens: CaseWhen[] = Array.isArray(cc.whens)
            ? (cc.whens as unknown[]).flatMap((w): CaseWhen[] => {
                if (typeof w !== 'object' || w === null) return []
                const ww = w as Record<string, unknown>
                return [{
                  nodeId: typeof ww.nodeId === 'string' ? ww.nodeId : '',
                  columnName: typeof ww.columnName === 'string' ? ww.columnName : '',
                  operator: (CASE_OPERATORS as string[]).includes(ww.operator as string)
                    ? (ww.operator as CaseOperator) : '=',
                  operandValue: typeof ww.operandValue === 'string' ? ww.operandValue : '',
                  thenValue: typeof ww.thenValue === 'string' ? ww.thenValue : '',
                }]
              })
            : []
          return [{
            id: typeof cc.id === 'string' ? cc.id : '',
            nodeId: typeof cc.nodeId === 'string' ? cc.nodeId : '',
            alias: typeof cc.alias === 'string' ? cc.alias : '',
            whens,
            elseValue: typeof cc.elseValue === 'string' ? cc.elseValue : '',
          }]
        })
      : []

    const calculatedColumns: CalculatedColumn[] = Array.isArray(p.calculatedColumns)
      ? (p.calculatedColumns as unknown[]).flatMap((c): CalculatedColumn[] => {
          if (typeof c !== 'object' || c === null) return []
          const cc = c as Record<string, unknown>
          return [{
            id: typeof cc.id === 'string' ? cc.id : '',
            nodeId: typeof cc.nodeId === 'string' ? cc.nodeId : '',
            alias: typeof cc.alias === 'string' ? cc.alias : '',
            expression: typeof cc.expression === 'string' ? cc.expression : '',
          }]
        })
      : []

    return { nodes, edges, filters, orderBy, caseColumns, calculatedColumns }
  } catch {
    return EMPTY_BUILDER_STATE
  }
}

// Private recursive helper for parseBuilderState — normalizes one FilterGroup level.
// Drops malformed items (flatMap → []); a nested group recurses, a condition is
// field-normalized. Not exported (the canvas only needs the top-level parse result).
function normalizeFilterGroup(raw: Record<string, unknown>): FilterGroup {
  return {
    id: typeof raw.id === 'string' ? raw.id : 'root',
    kind: 'group',
    combinator: raw.combinator === 'OR' ? 'OR' : 'AND',
    items: Array.isArray(raw.items)
      ? (raw.items as unknown[]).flatMap((item): (FilterCondition | FilterGroup)[] => {
          if (typeof item !== 'object' || item === null) return []
          const it = item as Record<string, unknown>
          if (it.kind === 'group') return [normalizeFilterGroup(it)]
          if (it.kind === 'condition') {
            return [{
              id: typeof it.id === 'string' ? it.id : '',
              kind: 'condition',
              tableName: typeof it.tableName === 'string' ? it.tableName : '',
              columnName: typeof it.columnName === 'string' ? it.columnName : '',
              operator: (CASE_OPERATORS as string[]).includes(it.operator as string)
                ? (it.operator as FilterOperator) : '=',
              value: Array.isArray(it.value)
                ? (it.value as unknown[]).filter((v): v is string => typeof v === 'string')
                : (it.value === null ? null : String(it.value ?? '')),
            }]
          }
          return []
        })
      : [],
  }
}

// Story 9.5: Validation gate for the left (FROM) table designation.
// Returns the i18n key when the builder state is invalid, or null when valid.
// Single source of truth — the canvas validation banner (Story 9.5) and the
// Save/Preview buttons (Stories 11.2/11.3) both consume this.
//
// Rule (FR-66 AC-3): only "more than one table with no Left" is invalid. A
// single table is its own FROM anchor (no designation required); zero tables is
// valid here (empty-canvas Save is gated separately in Epic 10/11). Typed against
// Pick<TableNodeState, 'data'>[] so it accepts both TableNodeState[] and the
// canvas's TableNodeType[] (which structurally satisfies { data: TableNodeData }).
export function getLeftTableValidationError(
  nodes: Pick<TableNodeState, 'data'>[],
): 'datasets.builder.validation.noLeftTable' | null {
  if (nodes.length <= 1) return null
  const hasLeft = nodes.some((n) => n.data.side === 'left')
  return hasLeft ? null : 'datasets.builder.validation.noLeftTable'
}

// Story 10.1: Validation gate for column selection.
// Returns the i18n key when no column is checked across any table node,
// or null when at least one column is checked.
// Single source of truth — the canvas validation banner (Story 10.1) and the
// Save/Preview buttons (Stories 11.2/11.3) both consume this.
//
// Rule (FR-67 J-1 AC-4): zero nodes is valid (no query to build); ≥1 node
// with zero checked columns across all nodes is invalid.
export function getColumnSelectionValidationError(
  nodes: Pick<TableNodeState, 'data'>[],
): 'datasets.builder.validation.noColumnsSelected' | null {
  if (nodes.length === 0) return null
  const hasChecked = nodes.some((n) => n.data.columns.some((c) => c.checked))
  return hasChecked ? null : 'datasets.builder.validation.noColumnsSelected'
}

// FR-57 identifier rules for column aliases (same as DatasetName / Decision 1.1):
// lowercase letters, digits, underscores; must start with letter or underscore; max 63 chars.
// Empty alias is valid — it means "use the default {table}_{column} at SQL generation time"
// (Story 11.1). Client-side validation only; server re-validates via SafeIdentifier.Create().
export const ALIAS_PATTERN = /^[a-z_][a-z0-9_]*$/

export function isValidAlias(alias: string): boolean {
  if (alias === '') return true
  return ALIAS_PATTERN.test(alias) && alias.length <= 63
}

// Story 10.3 AC-4: gate Save/Preview when any CASE column has an empty alias.
// Returns the i18n key when invalid, null when valid.
// SSOT — consumed by the canvas validation banner (Story 10.3) and by Stories
// 11.2/11.3 to disable Save/Preview buttons.
export function getCaseColumnAliasError(
  caseColumns: CaseColumn[],
): 'datasets.builder.validation.caseColumnRequiresAlias' | null {
  return caseColumns.some((c) => c.alias.trim() === '')
    ? 'datasets.builder.validation.caseColumnRequiresAlias'
    : null
}

// Story 10.4 AC-3: gate Save/Preview when any calculated column has an empty alias.
// Returns the i18n key when invalid, null when valid.
// SSOT — consumed by the canvas validation banner (Story 10.4) and by Stories
// 11.2/11.3 to disable Save/Preview buttons.
export function getCalculatedColumnAliasError(
  calculatedColumns: CalculatedColumn[],
): 'datasets.builder.validation.calculatedColumnRequiresAlias' | null {
  return calculatedColumns.some((c) => c.alias.trim() === '')
    ? 'datasets.builder.validation.calculatedColumnRequiresAlias'
    : null
}
