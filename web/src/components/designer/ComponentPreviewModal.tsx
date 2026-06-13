import { useMemo } from 'react'
import type { ComponentSchemaDto, DesignerElement } from '@/types/designer'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import DynamicComponent from './DynamicComponent'

interface ComponentPreviewModalProps {
  open: boolean
  onClose: () => void
  designerId: string
  rootElement: DesignerElement | null
  displayName?: string
  // When the designer is fullscreen, the modal's default body-portal becomes
  // invisible (the browser only shows the fullscreen element). The host page
  // passes the fullscreen element here so the dialog portals inside it. Null
  // / undefined means "default to document.body".
  portalContainer?: HTMLElement | null
}

export default function ComponentPreviewModal({
  open,
  onClose,
  designerId,
  rootElement,
  displayName,
  portalContainer,
}: ComponentPreviewModalProps) {
  // Synthesize a ComponentSchemaDto from the in-memory canvas tree so
  // DynamicComponent's `enabled: !!designerId && !providedSchema` short-circuit
  // skips its internal authenticated GET — the live canvas is the source of
  // truth here, not a backend version. Read-only semantics come for free:
  // omitting `onSave` makes DynamicComponent.handlePrimaryButtonClick a no-op
  // (AC-2), and DynamicComponent runs the same computeVisibility path used in
  // production (AC-3) and routes Repeater leaves through ElementRenderer with
  // interactive={true} so the "Add row" control is always rendered (AC-4).
  const previewSchema = useMemo<ComponentSchemaDto | null>(
    () =>
      rootElement
        ? {
            designerId,
            displayName: displayName ?? '',
            // Mode is irrelevant to the read-only preview render; CRUD is a safe stand-in.
            mode: 'CRUD',
            status: 'Draft',
            latestVersion: 1,
            rootElement,
            createdAt: '',
            updatedAt: null,
            publishedAt: null,
          }
        : null,
    [designerId, displayName, rootElement],
  )

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) onClose() }}>
      <DialogContent
        portalContainer={portalContainer}
        className="flex max-w-[90vw] flex-col gap-0 p-0 sm:max-w-[90vw]"
        style={{ maxHeight: '85vh' }}
      >
        <DialogHeader className="shrink-0 border-b border-border px-6 py-3">
          <DialogTitle className="truncate">
            {displayName ? `Preview · ${displayName}` : 'Component Preview'}
          </DialogTitle>
        </DialogHeader>
        <div className="flex-1 overflow-y-auto px-6 py-5">
          {previewSchema ? (
            <DynamicComponent
              designerId={designerId}
              schema={previewSchema}
              initialData={{}}
            />
          ) : (
            <div className="flex h-32 items-center justify-center text-sm text-muted-foreground">
              Nothing to preview — canvas is empty.
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
