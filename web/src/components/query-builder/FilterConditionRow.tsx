import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  FILTER_OPERATORS,
  type FilterCondition,
  type FilterOperator,
} from '../../features/datasets/types/builderState'
import type { TableNodeType } from './TableNode'

interface FilterConditionRowProps {
  condition: FilterCondition
  nodes: TableNodeType[]
  onChange: (updated: FilterCondition) => void
  onDelete: () => void
}

// Operator → empty-value shape. Centralized so table/column/operator changes all
// reset `value` to the right shape (AC-3): null for IS NULL/IS NOT NULL, string[]
// for IN/NOT IN, two-element string[] for BETWEEN, '' for everything else.
const OPERATORS_NULL_VALUE: FilterOperator[] = ['IS NULL', 'IS NOT NULL']
const OPERATORS_ARRAY_VALUE: FilterOperator[] = ['IN', 'NOT IN']
const OPERATORS_BETWEEN_VALUE: FilterOperator[] = ['BETWEEN']

function emptyValueForOperator(op: FilterOperator): string | string[] | null {
  if (OPERATORS_NULL_VALUE.includes(op)) return null
  if (OPERATORS_ARRAY_VALUE.includes(op)) return []
  if (OPERATORS_BETWEEN_VALUE.includes(op)) return ['', '']
  return ''
}

// PG type → HTML input type (AC-4). The pgType string comes from
// information_schema.columns.data_type via the catalog endpoint, so both the
// canonical forms ('integer', 'timestamp with time zone', …) and the short
// internal forms ('int4', 'timestamptz', …) are matched defensively.
function inputTypeForPgType(
  pgType: string,
): 'text' | 'number' | 'datetime-local' | 'date' | 'checkbox' {
  const lower = pgType.toLowerCase()
  if (
    [
      'integer', 'bigint', 'smallint', 'real', 'double precision',
      'numeric', 'decimal', 'money',
      'int4', 'int8', 'int2', 'float4', 'float8',
    ].includes(lower)
  )
    return 'number'
  if (
    [
      'timestamp with time zone', 'timestamptz',
      'timestamp without time zone', 'timestamp',
    ].includes(lower)
  )
    return 'datetime-local'
  if (lower === 'date') return 'date'
  if (lower === 'boolean' || lower === 'bool') return 'checkbox'
  return 'text'
}

const SELECT_CLASS =
  'h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring'
const INPUT_CLASS =
  'h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring'

// Story 10.6 (FR-68 J-6): a single filter condition row — table + column +
// operator + adaptive value input. Replaces the Story 10.5 "(Condition)"
// placeholder inside FilterConditionsDialog. Not a React Flow node, so the
// v12 NodeProps/typing rules do not apply here.
export function FilterConditionRow({
  condition,
  nodes,
  onChange,
  onDelete,
}: FilterConditionRowProps) {
  const { t } = useTranslation()

  const tableColumns =
    nodes.find((n) => n.data.tableName === condition.tableName)?.data.columns ?? []
  const pgType =
    tableColumns.find((c) => c.columnName === condition.columnName)?.pgType ?? 'text'
  const inputType = inputTypeForPgType(pgType)

  function handleTableChange(tableName: string) {
    const firstCol =
      nodes.find((n) => n.data.tableName === tableName)?.data.columns[0]?.columnName ?? ''
    onChange({
      ...condition,
      tableName,
      columnName: firstCol,
      value: emptyValueForOperator(condition.operator),
    })
  }

  function handleColumnChange(columnName: string) {
    // pgType may differ from the previous column, so the stored value could be a
    // type mismatch — reset it to the operator's empty shape.
    onChange({ ...condition, columnName, value: emptyValueForOperator(condition.operator) })
  }

  function handleOperatorChange(operator: FilterOperator) {
    onChange({ ...condition, operator, value: emptyValueForOperator(operator) })
  }

  function handleValueChange(value: string | string[] | null) {
    onChange({ ...condition, value })
  }

  function renderValueInput() {
    const op = condition.operator

    // IS NULL / IS NOT NULL — no value input.
    if (OPERATORS_NULL_VALUE.includes(op)) return null

    // IN / NOT IN — comma-separated text; stored as string[].
    if (OPERATORS_ARRAY_VALUE.includes(op)) {
      const arr = Array.isArray(condition.value) ? condition.value : []
      return (
        <label className="flex flex-col gap-0.5">
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.valueLabel')}
          </span>
          <input
            type="text"
            value={arr.join(', ')}
            onChange={(e) =>
              handleValueChange(
                e.target.value
                  .split(',')
                  .map((s) => s.trim())
                  .filter(Boolean),
              )
            }
            aria-label={t('datasets.builder.filterDialog.valueLabel')}
            className={INPUT_CLASS}
          />
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.valueInHint')}
          </span>
        </label>
      )
    }

    // BETWEEN — two type-adaptive inputs ([from, to]). Boolean BETWEEN is
    // degenerate, so a checkbox pgType falls back to text inputs here.
    if (OPERATORS_BETWEEN_VALUE.includes(op)) {
      const arr = Array.isArray(condition.value) ? condition.value : ['', '']
      const from = arr[0] ?? ''
      const to = arr[1] ?? ''
      const betweenType = inputType === 'checkbox' ? 'text' : inputType
      return (
        <div className="flex gap-1.5">
          <label className="flex flex-1 flex-col gap-0.5">
            <span className="text-xs text-muted-foreground">
              {t('datasets.builder.filterDialog.valueBetweenFrom')}
            </span>
            <input
              type={betweenType}
              value={from}
              onChange={(e) => handleValueChange([e.target.value, to])}
              aria-label={t('datasets.builder.filterDialog.valueBetweenFrom')}
              className={INPUT_CLASS}
            />
          </label>
          <label className="flex flex-1 flex-col gap-0.5">
            <span className="text-xs text-muted-foreground">
              {t('datasets.builder.filterDialog.valueBetweenTo')}
            </span>
            <input
              type={betweenType}
              value={to}
              onChange={(e) => handleValueChange([from, e.target.value])}
              aria-label={t('datasets.builder.filterDialog.valueBetweenTo')}
              className={INPUT_CLASS}
            />
          </label>
        </div>
      )
    }

    // All other operators — single type-adaptive input.
    const single = typeof condition.value === 'string' ? condition.value : ''
    if (inputType === 'checkbox') {
      return (
        <label className="flex items-center gap-1.5">
          <input
            type="checkbox"
            checked={single === 'true'}
            onChange={(e) => handleValueChange(e.target.checked ? 'true' : 'false')}
            aria-label={t('datasets.builder.filterDialog.valueLabel')}
            className="h-3.5 w-3.5 cursor-pointer"
          />
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.valueLabel')}
          </span>
        </label>
      )
    }
    return (
      <label className="flex flex-col gap-0.5">
        <span className="text-xs text-muted-foreground">
          {t('datasets.builder.filterDialog.valueLabel')}
        </span>
        <input
          type={inputType}
          value={single}
          onChange={(e) => handleValueChange(e.target.value)}
          aria-label={t('datasets.builder.filterDialog.valueLabel')}
          className={INPUT_CLASS}
        />
      </label>
    )
  }

  return (
    <div className="flex flex-1 flex-col gap-1.5 rounded border border-border bg-card px-3 py-1.5">
      <div className="flex items-start gap-1.5">
        {/* Table selector */}
        <label className="flex flex-1 flex-col gap-0.5">
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.tableLabel')}
          </span>
          <select
            value={condition.tableName}
            onChange={(e) => handleTableChange(e.target.value)}
            aria-label={t('datasets.builder.filterDialog.tableLabel')}
            className={SELECT_CLASS}
          >
            {nodes.map((n) => (
              <option key={n.id} value={n.data.tableName}>
                {n.data.tableName}
              </option>
            ))}
          </select>
        </label>

        {/* Column selector */}
        <label className="flex flex-1 flex-col gap-0.5">
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.columnLabel')}
          </span>
          <select
            value={condition.columnName}
            onChange={(e) => handleColumnChange(e.target.value)}
            aria-label={t('datasets.builder.filterDialog.columnLabel')}
            disabled={tableColumns.length === 0}
            className={SELECT_CLASS}
          >
            {tableColumns.length === 0 ? (
              <option value="">{t('datasets.builder.filterDialog.noColumnsOption')}</option>
            ) : (
              tableColumns.map((c) => (
                <option key={c.columnName} value={c.columnName}>
                  {c.columnName}
                </option>
              ))
            )}
          </select>
        </label>

        {/* Operator selector */}
        <label className="flex flex-1 flex-col gap-0.5">
          <span className="text-xs text-muted-foreground">
            {t('datasets.builder.filterDialog.operatorLabel')}
          </span>
          <select
            value={condition.operator}
            onChange={(e) => handleOperatorChange(e.target.value as FilterOperator)}
            aria-label={t('datasets.builder.filterDialog.operatorLabel')}
            className={SELECT_CLASS}
          >
            {FILTER_OPERATORS.map((op) => (
              <option key={op} value={op}>
                {op}
              </option>
            ))}
          </select>
        </label>

        {/* Delete condition */}
        <button
          type="button"
          onClick={onDelete}
          aria-label={t('datasets.builder.filterDialog.deleteConditionAria')}
          className="mt-[1.125rem] flex h-4 w-4 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          <X className="h-3 w-3" />
        </button>
      </div>

      {/* Adaptive value input */}
      {renderValueInput()}
    </div>
  )
}
