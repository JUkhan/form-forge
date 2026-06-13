import { useState, useCallback, useEffect, useRef } from 'react'
import { createFileRoute, Link } from '@tanstack/react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useTranslation } from 'react-i18next'
import { ChevronLeft, Database, Maximize2, Minimize2 } from 'lucide-react'
import {
  getDataset,
  updateDataset,
  previewDataset,
  type PreviewResult,
} from '../../../features/datasets/datasetApi'
import { QueryBuilderCanvas } from '../../../features/datasets/QueryBuilderCanvas'
import { PreviewResultDialog } from '../../../features/datasets/PreviewResultDialog'
import { TablePalette } from '../../../features/datasets/TablePalette'
import {
  parseBuilderState,
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
} from '../../../features/datasets/types/builderState'
import type { BuilderState } from '../../../features/datasets/types/builderState'
import { ApiError } from '../../../lib/api/apiError'
import { DATASETS_LIST_QUERY_KEY } from '../../../features/datasets/useDatasetListQuery'
import { Button } from '@/components/ui/button'

// Trailing underscore on `datasets_` opts this route OUT of the datasets.tsx parent
// layout (which does NOT render an <Outlet />). The URL path stays /admin/datasets/$id.
export const Route = createFileRoute('/_app/admin/datasets_/$id')({
  loader: async ({ params }) => {
    const dataset = await getDataset(params.id)
    return { dataset }
  },
  component: DatasetBuilderPage,
  errorComponent: ({ error }) => (
    <div className="flex h-full items-center justify-center p-8">
      <p className="text-sm text-destructive">
        {error instanceof Error ? error.message : 'Failed to load dataset.'}
      </p>
    </div>
  ),
})

function DatasetBuilderPage() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const { dataset } = Route.useLoaderData()
  const [builderState, setBuilderState] = useState<BuilderState>(
    parseBuilderState(dataset.builderState),
  )
  // Local version tracking: the loader fetches the dataset once; after each Save the
  // response carries the incremented version. Holding it in state lets subsequent saves
  // (without a page reload) pass the correct expected version and avoid a 409 (Dev Notes §2).
  const [currentVersion, setCurrentVersion] = useState(dataset.version)

  const saveMutation = useMutation({
    mutationFn: (state: BuilderState) =>
      updateDataset(dataset.id, {
        isCustomQuery: false,
        builderState: JSON.stringify(state),
        version: currentVersion,
      }),
    onSuccess: (saved) => {
      setCurrentVersion(saved.version)
      void queryClient.invalidateQueries({ queryKey: DATASETS_LIST_QUERY_KEY })
      void queryClient.invalidateQueries({ queryKey: ['datasets', dataset.id] })
      toast.success(t('datasets.builder.saveSuccess'))
    },
    onError: (err) => {
      if (
        err instanceof ApiError &&
        err.status === 422 &&
        err.code === 'BUILDER_STATE_INVALID'
      ) {
        toast.error(err.detail?.trim() ? err.detail : t('datasets.builderStateInvalid'))
        return
      }
      if (
        err instanceof ApiError &&
        err.status === 409 &&
        err.code === 'DATASET_CONCURRENCY_CONFLICT'
      ) {
        toast.error(t('datasets.concurrencyConflict'))
        return
      }
      toast.error(t('errors.genericError'))
    },
  })

  // Save-button gate — mirrors the canvas's validation banners without duplicating their
  // rendering. The canvas still shows the banners independently (SSOT helpers).
  const hasValidationError =
    builderState.nodes.length === 0 ||
    !!getLeftTableValidationError(builderState.nodes) ||
    !!getColumnSelectionValidationError(builderState.nodes) ||
    !!getCaseColumnAliasError(builderState.caseColumns) ||
    !!getCalculatedColumnAliasError(builderState.calculatedColumns)

  // Story 11.3 (FR-72 AC-3/4/5/6) — read-only LIMIT-10 preview of the builder-mode query.
  // The server re-derives SQL from builder_state, so we send the current canvas state.
  const [previewResult, setPreviewResult] = useState<PreviewResult | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewOpen, setPreviewOpen] = useState(false)

  const previewMutation = useMutation({
    mutationFn: () =>
      previewDataset({
        isCustomQuery: false,
        builderState: JSON.stringify(builderState),
      }),
    onSuccess: (data) => {
      setPreviewResult(data)
      setPreviewError(null)
    },
    onError: (err) => {
      setPreviewResult(null)
      if (err instanceof ApiError) {
        setPreviewError(err.detail?.trim() ? err.detail : t('errors.genericError'))
      } else {
        setPreviewError(t('errors.genericError'))
      }
    },
  })

  // Clear stale preview when the canvas state changes (mirrors the custom-query path in datasets.tsx).
  useEffect(() => {
    setPreviewResult(null)
    setPreviewError(null)
  }, [builderState])

  const handleStateChange = useCallback((state: BuilderState) => {
    setBuilderState(state)
  }, [])

  // Browser fullscreen for the whole builder (canvas + palette + toolbar). The
  // container element is the fullscreen target so the Preview dialog can portal
  // into it (Radix's default document.body is hidden by the browser while another
  // element is fullscreen). `fullscreenchange` keeps the toggle in sync with ESC.
  const containerRef = useRef<HTMLDivElement>(null)
  // Hold the active fullscreen element in state (not the ref) so render never reads
  // a ref. Only the builder container ever requests fullscreen on this page, so a
  // non-null value means the builder is fullscreen.
  const [fullscreenEl, setFullscreenEl] = useState<HTMLElement | null>(null)
  const isFullscreen = fullscreenEl !== null

  useEffect(() => {
    const onChange = () => setFullscreenEl(document.fullscreenElement as HTMLElement | null)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) {
      void document.exitFullscreen()
    } else {
      void containerRef.current?.requestFullscreen()
    }
  }, [])

  return (
    <div ref={containerRef} className="flex h-screen flex-col bg-background">
      {/* Page header */}
      <header className="flex items-center gap-3 border-b border-border bg-background px-4 py-3">
        <Link
          to="/admin/datasets"
          className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ChevronLeft className="h-4 w-4" />
          {t('admin.datasets.navTitle')}
        </Link>
        <span className="text-muted-foreground">/</span>
        <div className="flex items-center gap-2">
          <Database className="h-4 w-4" />
          <span className="font-medium">{dataset.datasetName}</span>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Button
            type="button"
            variant="outline"
            size="icon-sm"
            onClick={toggleFullscreen}
            aria-label={
              isFullscreen
                ? t('datasets.builder.fullscreenExit')
                : t('datasets.builder.fullscreenEnter')
            }
            title={
              isFullscreen
                ? t('datasets.builder.fullscreenExit')
                : t('datasets.builder.fullscreenEnter')
            }
          >
            {isFullscreen ? (
              <Minimize2 className="h-4 w-4" />
            ) : (
              <Maximize2 className="h-4 w-4" />
            )}
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={builderState.nodes.length === 0 || previewMutation.isPending}
            onClick={() => {
              setPreviewOpen(true)
              previewMutation.mutate()
            }}
          >
            {previewMutation.isPending
              ? t('datasets.builder.previewingButton')
              : t('datasets.builder.previewButton')}
          </Button>
          <Button
            size="sm"
            disabled={hasValidationError || saveMutation.isPending}
            onClick={() => saveMutation.mutate(builderState)}
          >
            {saveMutation.isPending
              ? t('datasets.builder.savingButton')
              : t('datasets.builder.saveButton')}
          </Button>
        </div>
      </header>

      {/* Canvas area */}
      <div className="flex flex-1 overflow-hidden">
        <TablePalette />
        <main className="flex-1 overflow-hidden">
          <QueryBuilderCanvas initialState={builderState} onChange={handleStateChange} />
        </main>
      </div>

      {/* Preview results (Story 11.3) — shown in a popup opened on Preview click. */}
      <PreviewResultDialog
        open={previewOpen}
        onOpenChange={setPreviewOpen}
        isPending={previewMutation.isPending}
        error={previewError}
        result={previewResult}
        portalContainer={fullscreenEl}
      />
    </div>
  )
}
