import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

interface ParameterInputDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  // Placeholder names discovered in the query ("_age", "_student_id", …).
  placeholders: string[]
  // Called with a JSON object string mapping each placeholder name to its entered value.
  // Values are sent as strings — the server binds them as `unknown`-typed parameters, so
  // they coerce to the comparison column's type (integer/uuid/date) like inline literals.
  onSubmit: (queryParameters: string) => void
  // Radix portals to document.body; the builder page passes its fullscreen container.
  portalContainer?: HTMLElement | null
}

// Parameterized-query feature — prompts the user to fill every {_placeholder} before a
// preview can run. Opened from the admin dataset form's Preview button when the SQL has
// placeholders.
export function ParameterInputDialog({
  open,
  onOpenChange,
  placeholders,
  onSubmit,
  portalContainer,
}: ParameterInputDialogProps) {
  const { t } = useTranslation()
  // Entered values persist across opens, so re-previewing after a query tweak keeps them.
  const [values, setValues] = useState<Record<string, string>>({})

  const handleSubmit = () => {
    const params: Record<string, string> = {}
    for (const name of placeholders) params[name] = values[name] ?? ''
    onSubmit(JSON.stringify(params))
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent portalContainer={portalContainer} className="max-w-md">
        <DialogHeader>
          <DialogTitle>{t('datasets.parameters.title')}</DialogTitle>
          <DialogDescription>{t('datasets.parameters.description')}</DialogDescription>
        </DialogHeader>

        <form
          onSubmit={(e) => {
            // This dialog is portaled to document.body but is still nested inside the dataset
            // form in the React tree — React bubbles the synthetic submit event up the React
            // tree (across the portal), which would otherwise trigger the outer form's submit
            // (saving the dataset). stopPropagation keeps the submit local to this dialog.
            e.preventDefault()
            e.stopPropagation()
            handleSubmit()
          }}
          className="space-y-3"
        >
          {placeholders.map((name) => (
            <div key={name} className="space-y-1.5">
              <Label htmlFor={`param-${name}`}>{name}</Label>
              <Input
                id={`param-${name}`}
                autoComplete="off"
                value={values[name] ?? ''}
                onChange={(e) => setValues((prev) => ({ ...prev, [name]: e.target.value }))}
              />
            </div>
          ))}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t('admin.datasets.cancelButton')}
            </Button>
            <Button type="submit">{t('datasets.parameters.runButton')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
