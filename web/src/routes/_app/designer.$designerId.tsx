import { useEffect, useRef, useState } from 'react'
import { createFileRoute, Link } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { z } from 'zod'
import {
  AlertCircle,
  AlertTriangle,
  ArrowLeft,
  CheckCircle2,
  Eye,
  Loader2,
  Maximize2,
  Minimize2,
  Save,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { designerApi } from '@/features/designer/designerApi'
import { useDesignerCanvasStore } from '@/store/designerCanvas'
import DesignerToolbar from '@/components/designer/DesignerToolbar'
import DesignerCanvas from '@/components/designer/DesignerCanvas'
import PropertyInspector from '@/components/designer/PropertyInspector'
import ComponentPreviewModal from '@/components/designer/ComponentPreviewModal'
import { collectFieldKeyErrors } from '@/components/designer/fieldKeyValidation'
import { collectPgTypeErrors } from '@/components/designer/pgTypes'
import {
  collectDatasetComponents,
  parseQueryParamBindings,
  unmappedParams,
} from '@/components/designer/queryParamBindings'
import { getDataset } from '@/features/datasets/datasetApi'
import { extractPlaceholders } from '@/features/datasets/queryParameters'
import { useKeyboardDnD } from '@/components/designer/useKeyboardDnD'
import type { DragData } from '@/components/designer/dnd'
import { ApiError } from '@/lib/api/apiError'
import type { ComponentSchemaDto, DesignerElement } from '@/types/designer'

// Optional ?version=N opens a specific version for editing. Omitted → latest.
// The library's per-version "Open in canvas" action navigates here with it.
const designerSearchSchema = z.object({
  version: z.coerce.number().int().min(1).optional(),
})

export const Route = createFileRoute('/_app/designer/$designerId')({
  validateSearch: designerSearchSchema,
  component: DesignerPage,
})

function DesignerPage() {
  const { t } = useTranslation()
  const { designerId } = Route.useParams()
  const { version: routeVersion } = Route.useSearch()
  const qc = useQueryClient()
  const [savedAt, setSavedAt] = useState<number | null>(null)
  // Per-element validation messages collected by `collectFieldKeyErrors` —
  // rendered as a bulleted list under the banner so the admin can fix every
  // violation in one pass (AC-2 / AC-3 "identifying the issue").
  const [fieldKeyErrors, setFieldKeyErrors] = useState<string[]>([])

  const schemaId = useDesignerCanvasStore((s) => s.schemaId)
  const version = useDesignerCanvasStore((s) => s.version)
  const displayName = useDesignerCanvasStore((s) => s.displayName)
  const isDirty = useDesignerCanvasStore((s) => s.isDirty)
  const rootElement = useDesignerCanvasStore((s) => s.rootElement)
  const setSchema = useDesignerCanvasStore((s) => s.setSchema)
  const updateDisplayName = useDesignerCanvasStore((s) => s.updateDisplayName)
  const resetCanvas = useDesignerCanvasStore((s) => s.resetCanvas)

  const [showPreview, setShowPreview] = useState(false)

  // Keyboard-driven DnD state lives here at the page level so the aria-live
  // region (rendered just below the header) and both panels (DesignerToolbar +
  // DesignerCanvas) share one source of truth — pickup originating in the
  // palette must be visible to the canvas DropZones, and vice versa.
  const kbdDnD = useKeyboardDnD<DragData>()

  // Focus management on pickup/commit/cancel transitions:
  //   null → non-null (pickup): jump focus to the first DropZone in the canvas
  //   non-null → null (commit): focus the newly-inserted element by id
  //   non-null → null (cancel, no insertedId): restore focus to originator
  // Tracking the previous state with a ref lets one effect see the transition.
  const prevPickedUpRef = useRef<DragData | null>(null)
  useEffect(() => {
    const wasPicked = prevPickedUpRef.current !== null
    const isPicked = kbdDnD.pickedUp !== null
    prevPickedUpRef.current = kbdDnD.pickedUp
    if (!wasPicked && isPicked) {
      requestAnimationFrame(() => {
        const root = document.querySelector('[data-testid="designer-canvas-root"]')
        const first = root?.querySelector<HTMLElement>('[data-dropzone]')
        first?.focus()
      })
      return
    }
    if (wasPicked && !isPicked) {
      const insertedId = kbdDnD.insertedIdRef.current
      if (insertedId !== null) {
        requestAnimationFrame(() => {
          const el = document.querySelector<HTMLElement>(
            `[data-element-id="${CSS.escape(insertedId)}"]`,
          )
          el?.focus()
        })
      } else {
        kbdDnD.originatorRef.current?.focus()
      }
    }
  }, [kbdDnD.pickedUp, kbdDnD.insertedIdRef, kbdDnD.originatorRef])

  // Page-level Escape handler — AC-5 requires Escape to cancel from anywhere.
  // The per-element Escape wiring only fires when focus is on a participant
  // (palette card, canvas element, DropZone); this catches Escape pressed
  // while focus is on PropertyInspector, header buttons, "+ Add Tab", etc.
  const cancelKbdDnD = kbdDnD.cancel
  const isPicking = kbdDnD.pickedUp !== null
  useEffect(() => {
    if (!isPicking) return
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') cancelKbdDnD(t('designer.keyboard.cancelled'))
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [isPicking, cancelKbdDnD, t])

  // Browser-native fullscreen on the page wrapper. requestFullscreen() takes
  // the whole viewport (no browser chrome, no app shell sidebar), giving the
  // user the maximum canvas working area. Listening to `fullscreenchange`
  // keeps the local toggle state in sync with ESC-to-exit and the browser's
  // own fullscreen UI.
  const pageRef = useRef<HTMLDivElement>(null)
  const [isFullscreen, setIsFullscreen] = useState(false)
  useEffect(() => {
    const handler = () => {
      const fsEl =
        document.fullscreenElement ??
        (document as Document & { webkitFullscreenElement?: Element }).webkitFullscreenElement ??
        null
      setIsFullscreen(fsEl === pageRef.current)
    }
    document.addEventListener('fullscreenchange', handler)
    document.addEventListener('webkitfullscreenchange', handler)
    return () => {
      document.removeEventListener('fullscreenchange', handler)
      document.removeEventListener('webkitfullscreenchange', handler)
    }
  }, [])
  function toggleFullscreen() {
    const el = pageRef.current
    if (!el) return
    const doc = document as Document & {
      webkitFullscreenElement?: Element
      webkitExitFullscreen?: () => Promise<void>
    }
    const inFs = document.fullscreenElement ?? doc.webkitFullscreenElement
    if (inFs) {
      // Swallow rejections — exitFullscreen rejects on a few obscure race
      // conditions, and there's nothing meaningful to surface to the user.
      ;(document.exitFullscreen?.() ?? doc.webkitExitFullscreen?.())?.catch(() => {})
    } else {
      const node = el as HTMLDivElement & { webkitRequestFullscreen?: () => Promise<void> }
      ;(node.requestFullscreen?.() ?? node.webkitRequestFullscreen?.())?.catch(() => {})
    }
  }

  const { data, isLoading, isError } = useQuery({
    queryKey: ['designer', 'schema', designerId, routeVersion ?? 'latest'],
    queryFn: () => designerApi.getSchema(designerId, routeVersion),
    enabled: !!designerId,
    // Schema editor pages treat the loaded schema as load-once: a background
    // refetch on window focus or reconnect would silently overwrite the user's
    // unsaved canvas edits.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  })

  // Single param-keyed effect: reset first, then load. Splitting these into
  // two effects let the load-effect re-apply the previous query's `data` on
  // designerId change before the cleanup ran.
  useEffect(() => {
    if (data) setSchema(data)
    return () => resetCanvas()
  }, [designerId, routeVersion, data, setSchema, resetCanvas])

  useEffect(() => {
    if (savedAt === null) return
    const id = window.setTimeout(() => setSavedAt(null), 3000)
    return () => window.clearTimeout(id)
  }, [savedAt])

  // Clear the field-key error banner as soon as the user touches the tree,
  // even before they re-attempt Save. Without this the banner sticks until
  // the next Save click, which trains users to ignore the indicator.
  useEffect(() => {
    setFieldKeyErrors([])
  }, [rootElement])

  type SaveVars = { displayName: string; rootElement: DesignerElement; version: number }
  const saveMutation = useMutation<ComponentSchemaDto, Error, SaveVars>({
    mutationFn: (vars) => {
      // Defensive: if the store somehow doesn't know its schemaId yet, fall
      // back to the URL param. Save now updates the CURRENT version in place
      // (PUT) rather than spawning a new Draft snapshot on every save — and it
      // carries displayName so a rename is persisted to the schema row.
      const id = schemaId ?? designerId
      return designerApi.updateVersion(id, vars.version, vars.rootElement, vars.displayName)
    },
    onSuccess: (newData, vars) => {
      // Compare current store state against the snapshot that was actually
      // sent (vars), not a render-time closure capture — the closure rebinds
      // each render to the latest rootElement, which would wrongly mark the
      // tree clean even if the user edited during the in-flight save.
      const current = useDesignerCanvasStore.getState()
      const stillSameTree = current.rootElement === vars.rootElement
      const stillSameName = current.displayName.trim() === vars.displayName
      // In-place save keeps the same version axis; track the returned version
      // anyway so the store stays in sync with the server's source of truth.
      useDesignerCanvasStore.setState({
        version: newData.latestVersion,
        isDirty: !(stillSameTree && stillSameName),
      })
      setFieldKeyErrors([])
      setSavedAt(Date.now())
      // refetchType: 'none' prevents the active ['designer','schema',designerId,…]
      // query from refetching by prefix-match and overwriting in-flight user edits.
      void qc.invalidateQueries({
        queryKey: ['designer', 'schema', designerId],
        refetchType: 'none',
      })
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
    },
  })

  const trimmedName = displayName.trim()
  const canSave = trimmedName.length > 0 && rootElement !== null

  async function handleSave() {
    if (!canSave || saveMutation.isPending) return
    // canSave guarantees rootElement is non-null; the explicit guard keeps
    // TypeScript happy when narrowing the SaveVars rootElement type.
    if (rootElement === null) return
    // In-place save targets the loaded version. `version` is set by setSchema on
    // load, so it's only null before the schema arrives (canSave is false then).
    if (version === null) return
    // Pre-validate fieldKeys client-side so the SPA shows the violation list
    // inline without a server round trip. The backend still re-validates and
    // returns 422 if anything slips past (e.g. a stale client missing the
    // PG reserved-keyword list).
    const errors = [...collectFieldKeyErrors(rootElement), ...collectPgTypeErrors(rootElement)]

    // Parameterized "query" datasets must have every {_placeholder} bound to a filter input
    // before the form can be saved. Detecting them needs the dataset's SQL — ensureQueryData
    // returns the inspector-cached detail or fetches it once.
    for (const el of collectDatasetComponents(rootElement)) {
      const datasetId =
        typeof el.properties.optionsDatasetId === 'string' ? el.properties.optionsDatasetId : ''
      if (datasetId === '') continue
      let detail
      try {
        detail = await qc.ensureQueryData({
          queryKey: ['dataset', 'detail', datasetId],
          queryFn: () => getDataset(datasetId),
          staleTime: 60_000,
        })
      } catch {
        continue // dataset unreadable here — let the backend re-validate on save
      }
      if (detail.queryType !== 'query') continue
      const placeholders = extractPlaceholders(detail.query)
      if (placeholders.length === 0) continue
      const missing = unmappedParams(
        parseQueryParamBindings(el.properties.queryParamBindings),
        placeholders,
      )
      if (missing.length > 0) {
        errors.push(t('designer.canvas.unmappedDatasetParams', { params: missing.join(', ') }))
      }
    }

    if (errors.length > 0) {
      setFieldKeyErrors(errors)
      return
    }
    setFieldKeyErrors([])
    saveMutation.mutate({
      displayName: trimmedName,
      rootElement,
      version,
    })
  }

  function extractSaveErrorMessage(err: Error | null): string {
    if (!err) return t('designer.canvas.saveError')
    // ApiError.messageKey is the i18n key the backend chose for this envelope
    // (designers.fieldKeyInvalid / designers.versionConflict / etc). Without
    // this branch the UI shows the raw `API error 422: FIELD_KEY_INVALID`
    // string from Error.prototype.message.
    if (err instanceof ApiError) {
      const translated = t(err.messageKey, { defaultValue: '' }) as string
      if (translated.length > 0) return translated
    }
    return err.message ?? t('designer.canvas.saveError')
  }

  return (
    // absolute inset-0 + the `relative` Outlet wrapper in _app.tsx anchors the
    // designer to the wrapper's padding-edge, escaping the p-4 that pads normal
    // pages. Fullscreen on pageRef still works — requestFullscreen overrides
    // CSS positioning when the element is taken out of flow.
    <div ref={pageRef} className="absolute inset-0 flex flex-col bg-background">
      {/* Header — h-14 left side: back link + name input + status chip. Right
          side: condensed status text + action buttons. Field-key validation
          errors moved to a dedicated banner row below so they don't crowd the
          action bar when the list is long. */}
      <div className="flex h-14 shrink-0 items-center justify-between gap-4 border-b border-border bg-card px-4">
        <div className="flex min-w-0 items-center gap-2">
          <Tooltip>
            <TooltipTrigger asChild>
              <Link
                to="/designer/library"
                className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
                aria-label={t('designer.canvas.backToLibrary')}
              >
                <ArrowLeft className="h-4 w-4" />
              </Link>
            </TooltipTrigger>
            <TooltipContent>{t('designer.canvas.backToLibrary')}</TooltipContent>
          </Tooltip>
          <Input
            value={displayName}
            onChange={(e) => updateDisplayName(e.target.value)}
            // Border-none keeps it reading as a title; hover/focus reveal a
            // bordered edit affordance so users discover it's editable.
            className="min-w-0 flex-1 truncate border-transparent bg-transparent text-lg font-semibold shadow-none hover:border-border hover:bg-overlay-hover focus-visible:border-ring focus-visible:bg-card focus-visible:ring-0"
            placeholder={t('designer.canvas.title')}
            aria-label={t('designer.canvas.componentDisplayNameAria')}
          />
          {isDirty && (
            <span className="shrink-0 rounded-full bg-primary/10 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wider text-primary">
              {t('designer.canvas.unsavedChanges')}
            </span>
          )}
          {savedAt !== null && (
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-emerald-100 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wider text-emerald-800">
              <CheckCircle2 className="h-3 w-3" />
              {t('designer.canvas.saved')}
            </span>
          )}
        </div>
        <div className="flex items-center gap-2">
          {saveMutation.isError && (
            <span className="flex max-w-xs items-center gap-1.5 truncate text-xs font-medium text-destructive">
              <AlertTriangle className="h-4 w-4 shrink-0" />
              <span className="truncate">{extractSaveErrorMessage(saveMutation.error)}</span>
            </span>
          )}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                variant="outline"
                size="icon-sm"
                onClick={toggleFullscreen}
                aria-label={isFullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
                aria-pressed={isFullscreen}
              >
                {isFullscreen ? <Minimize2 className="h-4 w-4" /> : <Maximize2 className="h-4 w-4" />}
              </Button>
            </TooltipTrigger>
            <TooltipContent>
              {isFullscreen ? 'Exit fullscreen (ESC)' : 'Enter fullscreen'}
            </TooltipContent>
          </Tooltip>
          {(() => {
            // Disabled buttons don't emit pointer events, so the trigger wraps a
            // span; the "why-disabled" hint only renders when there is a message.
            const previewHint =
              rootElement === null ? 'Add at least one element to preview' : undefined
            return (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="inline-flex">
                    <Button
                      type="button"
                      variant="outline"
                      onClick={() => setShowPreview(true)}
                      disabled={rootElement === null}
                    >
                      <Eye className="h-4 w-4" />
                      {t('designer.canvas.previewButton')}
                    </Button>
                  </span>
                </TooltipTrigger>
                {previewHint && <TooltipContent>{previewHint}</TooltipContent>}
              </Tooltip>
            )
          })()}
          {(() => {
            const saveHint = !isDirty
              ? t('designer.canvas.saved')
              : !canSave
                ? trimmedName.length === 0
                  ? 'Add a name before saving'
                  : 'Add at least one element before saving'
                : undefined
            return (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="inline-flex">
          <Button
            type="button"
            onClick={() => void handleSave()}
            // Always visible (rather than only-when-dirty) so the affordance
            // is stable and the user can dry-run a Save validation. Disabled
            // state communicates why via the tooltip.
            disabled={saveMutation.isPending || !canSave || !isDirty}
          >
            {saveMutation.isPending ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Save className="h-4 w-4" />
            )}
            {t('designer.canvas.saveButton')}
          </Button>
                  </span>
                </TooltipTrigger>
                {saveHint && <TooltipContent>{saveHint}</TooltipContent>}
              </Tooltip>
            )
          })()}
        </div>
      </div>

      {/* Field-key validation banner — only renders on Save-blocked attempts.
          Lives outside the h-14 header so the list of violations can grow
          without pushing the action buttons out. */}
      {fieldKeyErrors.length > 0 && (
        <div
          role="alert"
          className="flex shrink-0 items-start gap-2 border-b border-destructive/30 bg-destructive/10 px-4 py-2 text-xs text-destructive"
        >
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <div className="min-w-0 flex-1">
            <p className="font-semibold">{t('designer.canvas.saveBlockedFieldKey')}</p>
            <ul className="mt-0.5 ml-4 list-disc space-y-0.5">
              {fieldKeyErrors.map((msg, i) => (
                <li key={`${i}-${msg}`}>{msg}</li>
              ))}
            </ul>
          </div>
        </div>
      )}

      <ComponentPreviewModal
        open={showPreview}
        onClose={() => setShowPreview(false)}
        designerId={designerId}
        rootElement={rootElement}
        displayName={trimmedName || t('designer.canvas.title')}
        // When the designer is fullscreen, the default body-portal is hidden
        // by the browser. Anchor the modal to pageRef instead so it renders
        // inside the visible fullscreen scope.
        portalContainer={isFullscreen ? pageRef.current : null}
      />

      {/* Live region for keyboard-DnD announcements. Sits OUTSIDE the body flex
          row so it doesn't take up layout space — `sr-only` keeps it in the
          accessibility tree while hiding it visually. */}
      <div role="status" aria-live="polite" aria-atomic="true" className="sr-only">
        {kbdDnD.announcement}
      </div>

      {/* Body — DesignerToolbar (left) + DesignerCanvas (center) + PropertyInspector (right) */}
      {isLoading ? (
        <div className="flex flex-1 overflow-hidden">
          <div className="w-60 shrink-0 border-r border-border bg-muted/60 p-4 space-y-2">
            {Array.from({ length: 8 }).map((_, i) => (
              <Skeleton key={i} className="h-10 w-full" />
            ))}
          </div>
          <div className="flex flex-1 items-center justify-center bg-muted/40">
            <div className="flex flex-col items-center gap-3 text-muted-foreground">
              <Loader2 className="h-6 w-6 animate-spin" />
              <p className="text-sm">{t('designer.canvas.loadingSchema')}</p>
            </div>
          </div>
          <aside className="w-80 shrink-0 border-l border-border bg-card p-4 space-y-2">
            <Skeleton className="h-5 w-32" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </aside>
        </div>
      ) : isError ? (
        <div className="flex flex-1 items-center justify-center bg-muted">
          <div className="flex max-w-md flex-col items-center gap-3 rounded-xl border border-destructive/30 bg-card p-8 text-center shadow-sm">
            <div className="flex h-12 w-12 items-center justify-center rounded-full bg-destructive/15 text-destructive">
              <AlertCircle className="h-6 w-6" />
            </div>
            <p className="text-sm font-medium text-foreground">
              {t('designer.canvas.loadError')}
            </p>
            <Link to="/designer/library" className="text-sm text-primary hover:underline">
              {t('designer.canvas.backToLibrary')}
            </Link>
          </div>
        </div>
      ) : (
        <div className="flex flex-1 overflow-hidden">
          <DesignerToolbar kbdDnD={kbdDnD} />
          <DesignerCanvas className="flex-1 bg-muted/40" kbdDnD={kbdDnD} />
          <aside className="w-80 shrink-0 overflow-y-auto border-l border-border bg-card p-4">
            <PropertyInspector />
          </aside>
        </div>
      )}
    </div>
  )
}
