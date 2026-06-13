import { useId } from 'react'
import { useTranslation } from 'react-i18next'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'

interface SqlQueryTextareaProps {
  id?: string
  value: string
  onChange: (value: string) => void
  disabled?: boolean
  error?: string
}

// Story 8.8 (FR-60 / FR-62 H-8 AC-3) — monospace SQL textarea for Custom Query
// mode. Consumed by the Dataset create/edit modal in Story 8.10. AC-3 submit-button
// disabling is form-level state in the parent modal (check `!query.trim()` there).
export function SqlQueryTextarea({ id: idProp, value, onChange, disabled, error }: SqlQueryTextareaProps) {
  const { t } = useTranslation()
  const generatedId = useId()
  const id = idProp ?? generatedId
  const errorId = `${id}-error`

  return (
    <div className="flex flex-col gap-1.5">
      <label
        htmlFor={id}
        className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
      >
        {t('datasets.sqlTextarea.label')}
      </label>
      <Textarea
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        placeholder={t('datasets.sqlTextarea.placeholder')}
        className={cn('font-mono text-sm min-h-[200px] resize-y', error && 'border-destructive')}
        aria-describedby={error ? errorId : undefined}
        aria-invalid={error ? true : undefined}
        spellCheck={false}
        autoComplete="off"
        autoCorrect="off"
      />
      {error && (
        <p id={errorId} className="text-sm text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}
