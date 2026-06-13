import { useTranslation } from 'react-i18next'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import type { PreviewResult } from './datasetApi'

interface PreviewResultDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  isPending: boolean
  error: string | null
  result: PreviewResult | null
  // Radix portals to document.body by default, which the browser hides when an
  // ancestor is fullscreen. The builder page passes its container so the popup
  // stays visible there; the form leaves it undefined (document.body).
  portalContainer?: HTMLElement | null
}

// Shared popup that renders dataset Preview results (LIMIT 10) for both the
// custom-query form and the visual Query Builder. The caller opens it on Preview
// click and drives the mutation; this component only renders the loading / error /
// table states from the passed-in props.
export function PreviewResultDialog({
  open,
  onOpenChange,
  isPending,
  error,
  result,
  portalContainer,
}: PreviewResultDialogProps) {
  const { t } = useTranslation()

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        portalContainer={portalContainer}
        className="max-h-[85vh] w-full max-w-4xl gap-3 overflow-hidden sm:max-w-4xl"
      >
        <DialogHeader>
          <DialogTitle>{t('datasets.previewDialogTitle')}</DialogTitle>
        </DialogHeader>

        {isPending && (
          <p className="py-6 text-center text-sm text-muted-foreground">
            {t('datasets.previewLoading')}
          </p>
        )}

        {!isPending && error && (
          <div
            role="alert"
            className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          >
            {error}
          </div>
        )}

        {!isPending && !error && result && (
          <div className="overflow-auto rounded-lg border border-border">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b bg-muted/50 text-left">
                  {result.columns.map((col, i) => (
                    <th key={col + '-' + i} className="px-3 py-2 font-medium">
                      {col}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.rows.length === 0 ? (
                  <tr>
                    <td
                      colSpan={result.columns.length}
                      className="px-3 py-4 text-center text-muted-foreground"
                    >
                      {t('datasets.previewNoRows')}
                    </td>
                  </tr>
                ) : (
                  result.rows.map((row, rowIdx) => (
                    <tr key={rowIdx} className="border-b last:border-0 hover:bg-overlay-hover">
                      {row.map((cell, cellIdx) => (
                        <td key={cellIdx} className="px-3 py-2">
                          {cell === null ? (
                            <span className="italic text-muted-foreground">null</span>
                          ) : (
                            String(cell)
                          )}
                        </td>
                      ))}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
            <p className="px-3 py-1.5 text-xs text-muted-foreground">
              {t('datasets.previewRowCount', { count: result.rows.length })}
            </p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
