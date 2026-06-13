import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import {
  isValidAlias,
  type CalculatedColumn,
} from '../../features/datasets/types/builderState'

interface CalculatedColumnEditorProps {
  calculatedColumn: CalculatedColumn
  onUpdate: (updated: CalculatedColumn) => void
  onClose: () => void
}

export function CalculatedColumnEditor({
  calculatedColumn,
  onUpdate,
  onClose,
}: CalculatedColumnEditorProps) {
  const { t } = useTranslation()
  const aliasError = calculatedColumn.alias !== '' && !isValidAlias(calculatedColumn.alias)
  const aliasErrorId = `calc-${calculatedColumn.id}-alias-error`
  const aliasRequiredId = `calc-${calculatedColumn.id}-alias-required`

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-80 overflow-y-auto rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      style={{ maxHeight: 'calc(100% - 2rem)' }}
      role="complementary"
      aria-label={t('datasets.builder.calculatedEditor.title')}
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">
          {t('datasets.builder.calculatedEditor.title')}
        </span>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('datasets.builder.calculatedEditor.closeAria')}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Expression */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.calculatedEditor.expressionLabel')}
          </p>
          <textarea
            value={calculatedColumn.expression}
            onChange={(e) => onUpdate({ ...calculatedColumn, expression: e.target.value })}
            aria-label={t('datasets.builder.calculatedEditor.expressionLabel')}
            placeholder={t('datasets.builder.calculatedEditor.expressionPlaceholder')}
            rows={3}
            className="nodrag nopan w-full resize-none rounded border border-border bg-background px-2 py-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
        </div>

        {/* Alias */}
        <div className="space-y-1">
          <p className="text-xs font-medium text-muted-foreground">
            {t('datasets.builder.calculatedEditor.aliasLabel')}
          </p>
          <input
            type="text"
            value={calculatedColumn.alias}
            onChange={(e) => onUpdate({ ...calculatedColumn, alias: e.target.value })}
            aria-label={t('datasets.builder.calculatedEditor.aliasLabel')}
            aria-invalid={aliasError || calculatedColumn.alias === ''}
            aria-describedby={
              calculatedColumn.alias === ''
                ? aliasRequiredId
                : aliasError
                  ? aliasErrorId
                  : undefined
            }
            placeholder="e.g. total_price"
            className={`h-7 w-full rounded border bg-background px-2 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
              aliasError ? 'border-destructive' : 'border-border'
            }`}
          />
          {calculatedColumn.alias === '' && (
            <p id={aliasRequiredId} className="text-xs text-destructive">
              {t('datasets.builder.calculatedEditor.aliasRequired')}
            </p>
          )}
          {aliasError && (
            <p id={aliasErrorId} className="text-xs text-destructive">
              {t('datasets.builder.node.aliasInvalidPattern')}
            </p>
          )}
        </div>
      </div>
    </div>
  )
}
