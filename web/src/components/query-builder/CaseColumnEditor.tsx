import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  CASE_OPERATORS,
  type CaseColumn,
  type CaseOperator,
  type CaseWhen,
} from '../../features/datasets/types/builderState'
import type { TableNodeType } from './TableNode'

interface CaseColumnEditorProps {
  caseColumn: CaseColumn
  nodes: TableNodeType[]
  onUpdate: (updated: CaseColumn) => void
  onClose: () => void
}

const OPERATORS_WITHOUT_VALUE: CaseOperator[] = ['IS NULL', 'IS NOT NULL']

export function CaseColumnEditor({ caseColumn, nodes, onUpdate, onClose }: CaseColumnEditorProps) {
  const { t } = useTranslation()
  const aliasError = caseColumn.alias !== '' && !isValidAlias(caseColumn.alias)
  const aliasErrorId = `case-${caseColumn.id}-alias-error`
  const aliasRequiredId = `case-${caseColumn.id}-alias-required`

  function updateWhen(index: number, patch: Partial<CaseWhen>) {
    onUpdate({
      ...caseColumn,
      whens: caseColumn.whens.map((w, i) => (i === index ? { ...w, ...patch } : w)),
    })
  }

  function addWhen() {
    const defaultNodeId = caseColumn.nodeId
    const defaultColumn =
      nodes.find((n) => n.id === defaultNodeId)?.data.columns[0]?.columnName ?? ''
    const newWhen: CaseWhen = {
      nodeId: defaultNodeId,
      columnName: defaultColumn,
      operator: '=',
      operandValue: '',
      thenValue: '',
    }
    onUpdate({ ...caseColumn, whens: [...caseColumn.whens, newWhen] })
  }

  function deleteWhen(index: number) {
    onUpdate({ ...caseColumn, whens: caseColumn.whens.filter((_, i) => i !== index) })
  }

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-80 overflow-y-auto rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      style={{ maxHeight: 'calc(100% - 2rem)' }}
      role="complementary"
      aria-label={t('datasets.builder.caseEditor.title')}
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">{t('datasets.builder.caseEditor.title')}</span>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('datasets.builder.caseEditor.closeAria')}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Alias */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.caseEditor.aliasLabel')}
          </p>
          <input
            type="text"
            value={caseColumn.alias}
            onChange={(e) => onUpdate({ ...caseColumn, alias: e.target.value })}
            aria-label={t('datasets.builder.caseEditor.aliasLabel')}
            aria-invalid={aliasError || caseColumn.alias === ''}
            aria-describedby={
              caseColumn.alias === '' ? aliasRequiredId : aliasError ? aliasErrorId : undefined
            }
            placeholder="e.g. price_category"
            className={`h-7 w-full rounded border bg-background px-2 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
              aliasError ? 'border-destructive' : 'border-border'
            }`}
          />
          {caseColumn.alias === '' && (
            <p id={aliasRequiredId} className="text-xs text-destructive">
              {t('datasets.builder.caseEditor.aliasRequired')}
            </p>
          )}
          {aliasError && (
            <p id={aliasErrorId} className="text-xs text-destructive">
              {t('datasets.builder.node.aliasInvalidPattern')}
            </p>
          )}
        </div>

        {/* WHEN arms */}
        <div className="space-y-2">
          {caseColumn.whens.map((when, index) => {
            const nodeColumns = nodes.find((n) => n.id === when.nodeId)?.data.columns ?? []
            const hasValue = !OPERATORS_WITHOUT_VALUE.includes(when.operator)
            return (
              <div key={index} className="space-y-1.5 rounded-md border border-border p-2">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-medium text-muted-foreground">
                    {t('datasets.builder.caseEditor.whenSection')} {index + 1}
                  </span>
                  {caseColumn.whens.length > 1 && (
                    <button
                      type="button"
                      onClick={() => deleteWhen(index)}
                      aria-label={t('datasets.builder.caseEditor.deleteWhenAria')}
                      className="flex h-4 w-4 items-center justify-center rounded text-muted-foreground hover:text-destructive focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    >
                      <X className="h-3 w-3" />
                    </button>
                  )}
                </div>

                {/* Table (node) selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.tableLabel')}
                  </p>
                  <select
                    value={when.nodeId}
                    onChange={(e) => {
                      const newNodeId = e.target.value
                      const firstCol =
                        nodes.find((n) => n.id === newNodeId)?.data.columns[0]?.columnName ?? ''
                      updateWhen(index, { nodeId: newNodeId, columnName: firstCol })
                    }}
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {nodes.map((n) => (
                      <option key={n.id} value={n.id}>
                        {n.data.tableName}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Column selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.columnLabel')}
                  </p>
                  <select
                    value={when.columnName}
                    onChange={(e) => updateWhen(index, { columnName: e.target.value })}
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {nodeColumns.map((c) => (
                      <option key={c.columnName} value={c.columnName}>
                        {c.columnName}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Operator selector */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.operatorLabel')}
                  </p>
                  <select
                    value={when.operator}
                    onChange={(e) =>
                      updateWhen(index, {
                        operator: e.target.value as CaseOperator,
                        operandValue: '',
                      })
                    }
                    className="h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    {CASE_OPERATORS.map((op) => (
                      <option key={op} value={op}>
                        {op}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Operand value (hidden for IS NULL / IS NOT NULL) */}
                {hasValue && (
                  <div className="space-y-0.5">
                    <p className="text-xs text-muted-foreground">
                      {t('datasets.builder.caseEditor.valueLabel')}
                    </p>
                    <input
                      type="text"
                      value={when.operandValue}
                      onChange={(e) => updateWhen(index, { operandValue: e.target.value })}
                      className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    />
                  </div>
                )}

                {/* THEN value */}
                <div className="space-y-0.5">
                  <p className="text-xs text-muted-foreground">
                    {t('datasets.builder.caseEditor.thenLabel')}
                  </p>
                  <input
                    type="text"
                    value={when.thenValue}
                    onChange={(e) => updateWhen(index, { thenValue: e.target.value })}
                    placeholder={t('datasets.builder.caseEditor.thenValuePlaceholder')}
                    className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  />
                </div>
              </div>
            )
          })}
        </div>

        {/* Add WHEN */}
        <button
          type="button"
          onClick={addWhen}
          className="w-full rounded border border-dashed border-border py-1 text-xs text-muted-foreground hover:bg-overlay-hover focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          {t('datasets.builder.caseEditor.addWhenButton')}
        </button>

        {/* ELSE */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.caseEditor.elseSection')}
          </p>
          <input
            type="text"
            value={caseColumn.elseValue}
            onChange={(e) => onUpdate({ ...caseColumn, elseValue: e.target.value })}
            placeholder={t('datasets.builder.caseEditor.elseValuePlaceholder')}
            className="h-6 w-full rounded border border-border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
        </div>
      </div>
    </div>
  )
}
