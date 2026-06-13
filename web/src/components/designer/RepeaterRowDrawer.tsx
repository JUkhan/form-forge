import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { X } from 'lucide-react'
import { cn } from '@/lib/utils'
import DynamicComponent from './DynamicComponent'

// Extracted from ElementRenderer.tsx in Story 3.9. The extraction breaks a
// would-be circular import: DynamicComponent.tsx imports ElementRenderer.tsx,
// so embedding DynamicComponent directly inside ElementRenderer creates a
// cycle. Living in its own file lets RepeaterRowDrawer mount DynamicComponent
// without dragging ElementRenderer through the cycle.
export default function RepeaterRowDrawer({
  designerId,
  version,
  mode,
  initialData,
  onSave,
  onClose,
  // Optional fieldKey to hide + exclude from the submitted payload (e.g. a TreeView's
  // auth-filter / owner column, which the backend stamps server-side).
  hiddenFieldKey,
}: {
  designerId: string
  version: number | undefined
  mode: 'add' | 'edit'
  initialData: Record<string, unknown>
  onSave: (data: Record<string, unknown>) => void
  onClose: () => void
  hiddenFieldKey?: string
}) {
  const { t } = useTranslation()
  // Right-side sliding drawer — overlay + panel that slides via translate-x.
  const [closing, setClosing] = useState(false)
  const handleClose = useCallback(() => setClosing(true), [])

  const submitRef = useRef<(() => void) | null>(null)
  const [isReady, setIsReady] = useState(false)
  const [isValid, setIsValid] = useState(false)

  // Guard against mousedown-inside-panel + drag-to-overlay + release-on-overlay
  // — without this, the bubbling `click` on the overlay would fire `handleClose`
  // and discard unsaved input. We only close when both mousedown AND click
  // originate on the overlay itself.
  const overlayMouseDownRef = useRef(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') handleClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [handleClose])

  // Belt-and-braces fallback unmount. The browser may drop `transitionend`
  // (interruption, parent re-layout, alt-tab); this guarantees onClose fires
  // by 350ms (300ms transition + 50ms safety) so the drawer can never leak.
  useEffect(() => {
    if (!closing) return
    const id = window.setTimeout(onClose, 350)
    return () => window.clearTimeout(id)
  }, [closing, onClose])

  const title = mode === 'edit' ? t('designer.repeaterDrawer.titleEdit') : t('designer.repeaterDrawer.titleAdd')
  const hasDesigner = designerId !== ''

  return (
    <div
      className={cn(
        'fixed inset-0 z-50 flex justify-end transition-colors duration-300 ease-out',
        closing ? 'bg-black/0 pointer-events-none' : 'bg-black/60',
      )}
      onMouseDown={(e) => {
        overlayMouseDownRef.current = e.target === e.currentTarget
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget && overlayMouseDownRef.current) {
          handleClose()
        }
        overlayMouseDownRef.current = false
      }}
    >
      <div
        className={cn(
          'flex h-full w-full max-w-md flex-col border-l border-border bg-card shadow-2xl transition-transform duration-300 ease-out',
          closing ? 'translate-x-full' : 'translate-x-0',
        )}
        onMouseDown={(e) => {
          // Reset the overlay ref BEFORE stopping propagation so that any
          // subsequent overlay click cannot see a stale `true` left over from
          // an earlier overlay-down → panel-up sequence (the panel's click
          // stopPropagation swallows the reset path in the overlay onClick).
          overlayMouseDownRef.current = false
          e.stopPropagation()
        }}
        onClick={(e) => e.stopPropagation()}
        onTransitionEnd={(e) => {
          if (closing && e.target === e.currentTarget && e.propertyName === 'transform') {
            onClose()
          }
        }}
      >
        <div className="flex shrink-0 items-center justify-between border-b border-border px-5 py-4">
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
          <button
            type="button"
            onClick={handleClose}
            className="rounded p-1 text-muted-foreground transition-colors hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
            aria-label={t('designer.repeaterDrawer.closeAria')}
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {hasDesigner ? (
            <DynamicComponent
              designerId={designerId}
              version={version}
              initialData={initialData}
              hiddenFieldKey={hiddenFieldKey}
              onSave={(data) => {
                onSave(data)
                handleClose()
              }}
              onValidityChange={setIsValid}
              onReadyChange={setIsReady}
              submitRef={submitRef}
            />
          ) : (
            <div className="rounded border border-dashed border-border p-4 text-xs text-muted-foreground">
              {t('designer.repeaterDrawer.noDesigner')}
            </div>
          )}
        </div>
        <div className="flex shrink-0 justify-end gap-2 border-t border-border px-5 py-3">
          <button
            type="button"
            onClick={handleClose}
            className="rounded px-3 py-1.5 text-sm text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active"
          >
            {t('designer.repeaterDrawer.cancel')}
          </button>
          <button
            type="button"
            onClick={() => submitRef.current?.()}
            disabled={!hasDesigner || !isReady || !isValid}
            className="rounded bg-primary px-3 py-1.5 text-sm text-primary-foreground hover:bg-primary-hover active:bg-primary-active disabled:opacity-50"
          >
            {t('designer.repeaterDrawer.save')}
          </button>
        </div>
      </div>
    </div>
  )
}
