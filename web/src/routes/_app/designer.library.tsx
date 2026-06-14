import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
} from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  Archive,
  CheckCircle2,
  ChevronDown,
  Copy,
  Database,
  Eye,
  ExternalLink,
  FilePlus,
  FolderOpen,
  Library,
  MoreHorizontal,
  Settings,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Combobox } from '@/components/ui/combobox'
import { ApiError } from '@/lib/api/apiError'
import { designerApi } from '@/features/designer/designerApi'
import { listDatasets } from '@/features/datasets/datasetApi'
import {
  getFieldKey,
  type ComponentSchemaDto,
  type ComponentSchemaListItem,
  type DesignerElement,
} from '@/types/designer'
import ComponentPreviewModal from '@/components/designer/ComponentPreviewModal'
import { SortHeader } from '@/components/shared/SortHeader'
import { SearchBox } from '@/components/shared/SearchBox'

const designerLibrarySearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  // "field:dir" single-column sort, e.g. "updatedAt:desc". Whitelisted server-side.
  sort: z.string().optional(),
  // Free-text query over designerId + displayName.
  q: z.string().optional(),
  // Latest-version status filter.
  status: z.enum(['Draft', 'Published', 'Archived']).optional(),
})

export const Route = createFileRoute('/_app/designer/library')({
  validateSearch: designerLibrarySearchSchema,
  component: DesignerLibraryPage,
})

const STATUS_STYLES: Record<
  ComponentSchemaListItem['status'],
  { chip: string; dot: string; labelKey: string }
> = {
  Published: { chip: 'bg-emerald-100 text-emerald-800', dot: 'bg-emerald-700', labelKey: 'designer.library.statusPublished' },
  Draft: { chip: 'bg-muted text-foreground', dot: 'bg-muted-foreground', labelKey: 'designer.library.statusDraft' },
  Archived: { chip: 'bg-muted text-muted-foreground', dot: 'bg-muted-foreground/60', labelKey: 'designer.library.statusArchived' },
}

// FR-54 — component mode badge styling (CRUD vs VIEW).
const MODE_STYLES: Record<'CRUD' | 'VIEW', { chip: string; labelKey: string }> = {
  CRUD: { chip: 'bg-blue-100 text-blue-800', labelKey: 'designer.library.modeCRUD' },
  VIEW: { chip: 'bg-violet-100 text-violet-800', labelKey: 'designer.library.modeVIEW' },
}

function formatRelative(iso: string | null): string {
  if (!iso) return '—'
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return '—'
  }
}

function StatusChip({ status }: { status: ComponentSchemaListItem['status'] }) {
  const { t } = useTranslation()
  const s = STATUS_STYLES[status]
  return (
    <span className={cn('inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium', s.chip)}>
      <span className={cn('h-1.5 w-1.5 rounded-full', s.dot)} aria-hidden />
      {t(s.labelKey)}
    </span>
  )
}

function ModeBadge({ mode }: { mode: ComponentSchemaListItem['mode'] }) {
  const { t } = useTranslation()
  const ms = MODE_STYLES[mode] ?? MODE_STYLES.CRUD
  return (
    <span className={cn('inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium', ms.chip)}>
      {t(ms.labelKey)}
    </span>
  )
}

function TableSkeleton({ cols }: { cols: number }) {
  return (
    <>
      {Array.from({ length: 5 }).map((_, i) => (
        <tr key={i}>
          <td colSpan={cols} className="px-5 py-4">
            <div className="h-4 w-full animate-pulse rounded bg-muted" />
          </td>
        </tr>
      ))}
    </>
  )
}

export function VersionFlyout({
  designerId,
  latestVersion,
}: {
  designerId: string
  latestVersion: number
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const [flyoutPos, setFlyoutPos] = useState<{ top: number; left: number } | null>(null)
  // Same overflow-clipping issue as RowMenu — see comment there. Portal out
  // and position with fixed coords.
  const FLYOUT_MIN_WIDTH_PX = 220
  const VIEWPORT_PADDING_PX = 8

  // Lazy load: only hit the API once the user expands the flyout. The full
  // schema GET returns versions[] (passing no version arg). Story 3.6 learning:
  // calling getSchema(id, version) returns the single-version DTO WITHOUT
  // versions[], so the no-arg form is mandatory here.
  const versionsQuery = useQuery({
    queryKey: ['designer', 'schema', designerId],
    queryFn: () => designerApi.getSchema(designerId),
    enabled: open,
    staleTime: 30_000,
  })

  useLayoutEffect(() => {
    if (!open) return
    const rect = triggerRef.current?.getBoundingClientRect()
    if (!rect) return
    const left = Math.max(
      VIEWPORT_PADDING_PX,
      Math.min(rect.left, window.innerWidth - FLYOUT_MIN_WIDTH_PX - VIEWPORT_PADDING_PX),
    )
    setFlyoutPos({ top: rect.bottom + 4, left })
  }, [open])

  useEffect(() => {
    if (!open) return
    const close = () => setOpen(false)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  const sorted = [...(versionsQuery.data?.versions ?? [])].sort(
    (a, b) => b.version - a.version,
  )

  return (
    <div className="inline-flex">
      <button
        ref={triggerRef}
        type="button"
        className="flex items-center gap-1 text-muted-foreground hover:text-foreground"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={t('designer.library.versionHistory')}
      >
        v{latestVersion}
        <ChevronDown className="h-3 w-3" />
      </button>

      {open && flyoutPos !== null &&
        createPortal(
          <>
            <div
              className="fixed inset-0 z-40"
              onClick={() => setOpen(false)}
              aria-hidden
            />
            <div
              role="listbox"
              className="fixed z-50 min-w-[220px] rounded-lg border border-border bg-card py-1 shadow-md"
              style={{ top: flyoutPos.top, left: flyoutPos.left }}
            >
              {versionsQuery.isPending && (
                <div className="px-3 py-2 text-xs text-muted-foreground">
                  {t('designer.library.versionHistoryLoading')}
                </div>
              )}
              {versionsQuery.isError && (
                <div role="alert" className="px-3 py-2 text-xs text-destructive">
                  {t('designer.library.versionHistoryError')}
                </div>
              )}
              {!versionsQuery.isPending && !versionsQuery.isError && sorted.length === 0 && (
                <div className="px-3 py-2 text-xs text-muted-foreground">
                  {t('designer.library.versionHistoryEmpty')}
                </div>
              )}
              {sorted.map((v) => (
                <div
                  key={v.version}
                  role="option"
                  aria-selected={false}
                  className="flex items-center gap-2 px-3 py-1.5 text-xs"
                >
                  <span className="w-7 font-mono text-muted-foreground">v{v.version}</span>
                  <StatusChip status={v.status} />
                  <span className="text-muted-foreground">{formatRelative(v.createdAt)}</span>
                </div>
              ))}
            </div>
          </>,
          document.body,
        )}
    </div>
  )
}

// Collects the user fieldKeys of a schema tree — the candidates for the auth
// filter field. Mirrors the backend's column set: Repeater / DatasetComponent
// subtrees are skipped (they are child relations / view-local inputs, not
// columns of this designer's table), so the list matches what the server will
// accept as a valid auth filter field key.
function collectAuthFilterFieldKeys(el: DesignerElement | null, out: string[] = []): string[] {
  if (!el) return out
  if (el.type === 'Repeater' || el.type === 'DatasetComponent') return out
  const key = getFieldKey(el.properties)
  if (key !== '' && !out.includes(key)) out.push(key)
  for (const child of el.children ?? []) collectAuthFilterFieldKeys(child, out)
  return out
}

// "Auth filter fieldKey set up" popup for a single designer version. Loads the
// version's schema (for the fieldKey list + current value) and binds a filterable
// combobox to the version's authFilterFieldKey. Selecting the "No filter" option
// clears it.
function AuthFilterDialog({
  designerId,
  version,
  onClose,
  qc,
}: {
  designerId: string
  version: number
  onClose: () => void
  qc: QueryClient
}) {
  const { t } = useTranslation()

  const schemaQuery = useQuery({
    queryKey: ['designer', 'schema', designerId, version],
    queryFn: () => designerApi.getSchema(designerId, version),
    staleTime: 60_000,
  })
  const schema = schemaQuery.data as ComponentSchemaDto | undefined

  const fieldKeys = useMemo(
    () => collectAuthFilterFieldKeys(schema?.rootElement ?? null),
    [schema?.rootElement],
  )

  // null = "not yet touched": fall back to the loaded value so the combobox
  // reflects the persisted key until the user changes it.
  const [selected, setSelected] = useState<string | null>(null)
  const value = selected ?? schema?.authFilterFieldKey ?? ''

  const options = useMemo(
    () => [
      { value: '', label: t('designer.library.authFilterNone') },
      ...fieldKeys.map((k) => ({ value: k, label: k })),
    ],
    [fieldKeys, t],
  )

  const mutation = useMutation({
    mutationFn: (next: string | null) =>
      designerApi.setAuthFilterFieldKey(designerId, version, next),
    onSuccess: () => {
      // Refresh both the library's schema cache (RowMenu / flyout) and the
      // renderer's component-schema cache so the hidden field updates live.
      void qc.invalidateQueries({ queryKey: ['designer', 'schema', designerId] })
      void qc.invalidateQueries({ queryKey: ['component-schema', designerId] })
      toast.success(t('designer.library.authFilterSuccess'))
      onClose()
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError && err.messageKey
          ? t(err.messageKey)
          : t('designer.library.authFilterError'),
      )
    },
  })

  return (
    <Dialog open onOpenChange={(next) => { if (!next) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {t('designer.library.authFilterTitle', { version })}
          </DialogTitle>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          {t('designer.library.authFilterDescription')}
        </p>
        {schemaQuery.isError ? (
          <div role="alert" className="text-sm text-destructive">
            {t('designer.library.authFilterLoadError')}
          </div>
        ) : (
          <div className="flex flex-col gap-1.5">
            <label htmlFor="auth-filter-field" className="text-sm font-medium text-foreground">
              {t('designer.library.authFilterFieldLabel')}
            </label>
            <Combobox
              id="auth-filter-field"
              value={value}
              onValueChange={setSelected}
              options={options}
              placeholder={t('designer.library.authFilterNone')}
              searchPlaceholder={t('designer.library.authFilterSearchPlaceholder')}
              emptyText={t('designer.library.authFilterEmpty')}
              loading={schemaQuery.isPending}
              aria-label={t('designer.library.authFilterFieldLabel')}
            />
          </div>
        )}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            type="button"
            onClick={() => mutation.mutate(value === '' ? null : value)}
            disabled={mutation.isPending || schemaQuery.isPending || schemaQuery.isError}
          >
            {t('common.save')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// "Dataset set up" popup for a single designer version. Binds a filterable combobox
// of all datasets to the version's datasetId. Selecting "No dataset" clears it (the
// record list reads from the provisioned table again). The description states the
// dataset convention so an author can build a compatible dataset: its columns must
// match the version's fieldKeys, and it must expose the record id as `<designerId>_id`.
function DatasetDialog({
  designerId,
  version,
  onClose,
  qc,
}: {
  designerId: string
  version: number
  onClose: () => void
  qc: QueryClient
}) {
  const { t } = useTranslation()

  // Load the version's schema only for its persisted datasetId (the current binding).
  const schemaQuery = useQuery({
    queryKey: ['designer', 'schema', designerId, version],
    queryFn: () => designerApi.getSchema(designerId, version),
    staleTime: 60_000,
  })
  const schema = schemaQuery.data as ComponentSchemaDto | undefined

  // The combobox source: all datasets. Small list, so fetch the first 100 like the
  // designer-backed Dropdown picker in PropertyInspector does.
  const datasetsQuery = useQuery({
    queryKey: ['datasets', 'list', 1, 100],
    queryFn: () => listDatasets(1, 100),
    staleTime: 60_000,
  })
  // null = "not yet touched": fall back to the persisted binding until the user changes it.
  const [selected, setSelected] = useState<string | null>(null)
  const value = selected ?? schema?.datasetId ?? ''

  const options = useMemo(() => {
    const datasets = datasetsQuery.data?.data ?? []
    const inList = datasets.some((d) => d.id === value)
    return [
      { value: '', label: t('designer.library.datasetNone') },
      // A persisted-but-missing dataset (deleted/renamed) still shows so the user
      // sees the current binding rather than a blank box.
      ...(!inList && value !== ''
        ? [{ value, label: t('designer.library.datasetMissing') }]
        : []),
      ...datasets.map((d) => ({ value: d.id, label: d.datasetName })),
    ]
  }, [datasetsQuery.data, value, t])

  const mutation = useMutation({
    mutationFn: (next: string | null) => designerApi.setDataset(designerId, version, next),
    onSuccess: () => {
      // Refresh the library's schema cache (RowMenu / flyout) and the renderer's
      // component-schema cache so the bound source updates live.
      void qc.invalidateQueries({ queryKey: ['designer', 'schema', designerId] })
      void qc.invalidateQueries({ queryKey: ['component-schema', designerId] })
      toast.success(t('designer.library.datasetSuccess'))
      onClose()
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError && err.messageKey
          ? t(err.messageKey)
          : t('designer.library.datasetError'),
      )
    },
  })

  return (
    <Dialog open onOpenChange={(next) => { if (!next) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {t('designer.library.datasetTitle', { version })}
          </DialogTitle>
        </DialogHeader>
        <p className="text-sm text-muted-foreground whitespace-pre-line">
          {t('designer.library.datasetDescription', { idColumn: `${designerId}_id` })}
        </p>
        {datasetsQuery.isError ? (
          <div role="alert" className="text-sm text-destructive">
            {t('designer.library.datasetLoadError')}
          </div>
        ) : (
          <div className="flex flex-col gap-1.5">
            <label htmlFor="dataset-binding" className="text-sm font-medium text-foreground">
              {t('designer.library.datasetFieldLabel')}
            </label>
            <Combobox
              id="dataset-binding"
              value={value}
              onValueChange={setSelected}
              options={options}
              placeholder={t('designer.library.datasetNone')}
              searchPlaceholder={t('designer.library.datasetSearchPlaceholder')}
              emptyText={t('designer.library.datasetEmpty')}
              loading={datasetsQuery.isPending || schemaQuery.isPending}
              aria-label={t('designer.library.datasetFieldLabel')}
            />
          </div>
        )}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            type="button"
            onClick={() => mutation.mutate(value === '' ? null : value)}
            disabled={mutation.isPending || datasetsQuery.isPending || datasetsQuery.isError}
          >
            {t('common.save')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export function RowMenu({ row, qc }: { row: ComponentSchemaListItem; qc: QueryClient }) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/designer/library' })
  const [open, setOpen] = useState(false)
  // The version whose Preview modal is open (null = closed). Per-version so the
  // user can preview any version listed in the menu, not just the latest.
  const [previewVersion, setPreviewVersion] = useState<number | null>(null)
  // The version whose "Auth filter fieldKey set up" dialog is open (null = closed).
  const [authFilterVersion, setAuthFilterVersion] = useState<number | null>(null)
  // The version whose "Dataset set up" dialog is open (null = closed).
  const [datasetVersion, setDatasetVersion] = useState<number | null>(null)
  const [showNewVersion, setShowNewVersion] = useState(false)

  // The menu must escape the table's overflow-x-auto wrapper (per CSS spec,
  // overflow-x:auto forces overflow-y to be non-visible too, so the wrapper
  // would clip the menu AND scroll vertically when it's opened on the bottom
  // rows). Portal the menu into document.body and position it via the trigger
  // button's getBoundingClientRect.
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const [menuPos, setMenuPos] = useState<{ top: number; left: number } | null>(null)
  const MENU_WIDTH_PX = 320 // matches w-80 — wide enough for v# + status + 3 actions
  const VIEWPORT_PADDING_PX = 8

  useLayoutEffect(() => {
    if (!open) return
    const updatePos = () => {
      const rect = triggerRef.current?.getBoundingClientRect()
      if (!rect) return
      // Right-align the menu to the trigger; clamp to viewport so the menu
      // doesn't get pushed off-screen on narrow viewports.
      const desiredLeft = rect.right - MENU_WIDTH_PX
      const left = Math.max(
        VIEWPORT_PADDING_PX,
        Math.min(desiredLeft, window.innerWidth - MENU_WIDTH_PX - VIEWPORT_PADDING_PX),
      )
      setMenuPos({ top: rect.bottom + 4, left })
    }
    updatePos()
  }, [open])

  // Close on scroll/resize: the absolute coordinates would otherwise drift away
  // from the trigger when the user scrolls the table or the page. The scroll
  // listener is capture-phase so it also catches scrolls inside nested
  // containers — but the menu's OWN version list is one of those, so ignore
  // scrolls that originate inside the menu (otherwise dragging that scrollbar
  // closes the popover).
  useEffect(() => {
    if (!open) return
    const onScroll = (e: Event) => {
      if (menuRef.current && e.target instanceof Node && menuRef.current.contains(e.target)) {
        return
      }
      setOpen(false)
    }
    const onResize = () => setOpen(false)
    window.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onResize)
    return () => {
      window.removeEventListener('scroll', onScroll, true)
      window.removeEventListener('resize', onResize)
    }
  }, [open])

  // Version list for the menu — lazy-loaded on open. The no-arg getSchema
  // returns versions[]; calling it with a version omits that array (Story 3.6).
  // Shares the cache key with VersionFlyout so they refetch together.
  const versionsQuery = useQuery({
    queryKey: ['designer', 'schema', row.designerId],
    queryFn: () => designerApi.getSchema(row.designerId),
    enabled: open,
    staleTime: 30_000,
  })
  const versions = [...(versionsQuery.data?.versions ?? [])].sort(
    (a, b) => b.version - a.version,
  )

  // Preview lazy-loads the chosen version's full schema — the list endpoint
  // returns ComponentSchemaListItem (no rootElement), so we fetch the specific
  // version's ComponentSchemaDto on demand.
  const previewSchemaQuery = useQuery({
    queryKey: ['designer', 'schema', row.designerId, previewVersion],
    queryFn: () => designerApi.getSchema(row.designerId, previewVersion!),
    enabled: previewVersion !== null,
    staleTime: 60_000,
  })

  // Resolve a backend ApiError to its localized message via messageKey, falling
  // back to a route-specific failure string (and then to the generic envelope)
  // for non-ApiError failures (e.g., a network drop). Centralized here so all
  // mutations stay consistent.
  const errorMessage = (err: unknown, fallbackKey: string): string => {
    if (err instanceof ApiError && err.messageKey) {
      return t(err.messageKey)
    }
    return t(fallbackKey)
  }

  const duplicateMutation = useMutation({
    mutationFn: () => designerApi.duplicateSchema(row.designerId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
      toast.success(t('designer.library.duplicateSuccess'))
      setOpen(false)
    },
    onError: (err) => {
      toast.error(errorMessage(err, 'designer.library.duplicateError'))
      setOpen(false)
    },
  })

  // New version: fetch the source's current rootElement and post it as a fresh
  // immutable Draft snapshot. A 422 here typically means the existing schema
  // contains a malformed fieldKey (edge case from older saves) — the toast
  // funnels the user to fix it in the canvas.
  const newVersionMutation = useMutation({
    mutationFn: async () => {
      const schema = await designerApi.getSchema(row.designerId)
      return designerApi.createVersion(row.designerId, schema.rootElement)
    },
    onSuccess: (data) => {
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
      // VersionFlyout / this menu cache versions[] under ['designer','schema', id]
      // with a 30s staleTime; without this, a freshly created version is invisible
      // until the cache expires.
      void qc.invalidateQueries({ queryKey: ['designer', 'schema', row.designerId] })
      toast.success(
        t('designer.library.newVersionSuccess', { version: data.latestVersion }),
      )
      setShowNewVersion(false)
      void navigate({
        to: '/designer/$designerId',
        params: { designerId: row.designerId },
        search: { version: data.latestVersion },
      })
    },
    onError: (err) => {
      toast.error(errorMessage(err, 'designer.library.newVersionError'))
      setShowNewVersion(false)
    },
  })

  // Publish a SPECIFIC version (passed as the mutation variable). A designer may
  // have multiple Published versions at once — publishing does NOT demote the
  // others. The menu stays open and the version list refetches so the status
  // flips in place.
  const publishMutation = useMutation({
    mutationFn: (version: number) => designerApi.publishVersion(row.designerId, version),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
      void qc.invalidateQueries({ queryKey: ['designer', 'schema', row.designerId] })
      toast.success(t('designer.library.publishSuccess'))
    },
    onError: (err) => {
      toast.error(errorMessage(err, 'designer.library.publishError'))
    },
  })

  // Archive (unpublish/retire) a SPECIFIC version — the explicit way to take a
  // Published version out of circulation now that publishing no longer demotes.
  const archiveMutation = useMutation({
    mutationFn: (version: number) => designerApi.archiveVersion(row.designerId, version),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
      void qc.invalidateQueries({ queryKey: ['designer', 'schema', row.designerId] })
      toast.success(t('designer.library.archiveSuccess'))
    },
    onError: (err) => {
      toast.error(errorMessage(err, 'designer.library.archiveError'))
    },
  })

  return (
    <div className="flex justify-end">
      <Button
        ref={triggerRef}
        variant="ghost"
        size="icon-sm"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={t('designer.library.rowMenuLabel')}
      >
        <MoreHorizontal className="h-4 w-4" />
      </Button>

      {open && menuPos !== null &&
        createPortal(
          <>
            {/* Click-outside layer — sits behind the menu but above the page. */}
            <div
              className="fixed inset-0 z-40"
              onClick={() => setOpen(false)}
              aria-hidden
            />
            <div
              ref={menuRef}
              role="menu"
              className="fixed z-50 w-80 overflow-hidden rounded-lg border border-border bg-card py-1 shadow-md"
              style={{ top: menuPos.top, left: menuPos.left }}
            >
              <div className="px-3 pb-1 pt-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                {t('designer.library.versionsHeader')}
              </div>

              <div className="max-h-64 overflow-y-auto">
                {versionsQuery.isPending && (
                  <div className="px-3 py-2 text-xs text-muted-foreground">
                    {t('designer.library.versionsLoading')}
                  </div>
                )}
                {versionsQuery.isError && (
                  <div role="alert" className="px-3 py-2 text-xs text-destructive">
                    {t('designer.library.versionsError')}
                  </div>
                )}
                {!versionsQuery.isPending && !versionsQuery.isError && versions.length === 0 && (
                  <div className="px-3 py-2 text-xs text-muted-foreground">
                    {t('designer.library.versionsEmpty')}
                  </div>
                )}
                {versions.map((v) => (
                  <div
                    key={v.version}
                    className="flex items-center gap-2 px-3 py-1.5 hover:bg-overlay-hover"
                  >
                    <span className="w-7 shrink-0 font-mono text-xs text-muted-foreground">
                      v{v.version}
                    </span>
                    <StatusChip status={v.status} />
                    <div className="ml-auto flex items-center gap-0.5">
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <button
                            type="button"
                            role="menuitem"
                            aria-label={t('designer.library.previewVersionAria', { version: v.version })}
                            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
                            onClick={() => {
                              setPreviewVersion(v.version)
                              setOpen(false)
                            }}
                          >
                            <Eye className="h-4 w-4" />
                          </button>
                        </TooltipTrigger>
                        <TooltipContent>{t('designer.library.preview')}</TooltipContent>
                      </Tooltip>
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Link
                            to="/designer/$designerId"
                            params={{ designerId: row.designerId }}
                            search={{ version: v.version }}
                            role="menuitem"
                            aria-label={t('designer.library.openVersionAria', { version: v.version })}
                            onClick={() => setOpen(false)}
                            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
                          >
                            <ExternalLink className="h-4 w-4" />
                          </Link>
                        </TooltipTrigger>
                        <TooltipContent>{t('designer.library.openInCanvas')}</TooltipContent>
                      </Tooltip>
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <button
                            type="button"
                            role="menuitem"
                            aria-label={t('designer.library.authFilterSetup', { version: v.version })}
                            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
                            onClick={() => {
                              setAuthFilterVersion(v.version)
                              setOpen(false)
                            }}
                          >
                            <Settings className="h-4 w-4" />
                          </button>
                        </TooltipTrigger>
                        <TooltipContent>{t('designer.library.authFilterSetup', { version: v.version })}</TooltipContent>
                      </Tooltip>
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <button
                            type="button"
                            role="menuitem"
                            aria-label={t('designer.library.datasetSetup', { version: v.version })}
                            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-foreground"
                            onClick={() => {
                              setDatasetVersion(v.version)
                              setOpen(false)
                            }}
                          >
                            <Database className="h-4 w-4" />
                          </button>
                        </TooltipTrigger>
                        <TooltipContent>{t('designer.library.datasetSetup', { version: v.version })}</TooltipContent>
                      </Tooltip>
                      {v.status === 'Published' ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button
                              type="button"
                              role="menuitem"
                              disabled={archiveMutation.isPending}
                              aria-label={t('designer.library.archiveVersionLabel', { version: v.version })}
                              className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active disabled:cursor-not-allowed disabled:opacity-50"
                              onClick={() => archiveMutation.mutate(v.version)}
                            >
                              <Archive className="h-3.5 w-3.5" />
                              {t('designer.library.archive')}
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>
                            {t('designer.library.archiveVersionLabel', { version: v.version })}
                          </TooltipContent>
                        </Tooltip>
                      ) : (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button
                              type="button"
                              role="menuitem"
                              disabled={publishMutation.isPending}
                              aria-label={t('designer.library.publishVersionLabel', { version: v.version })}
                              className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium text-emerald-700 hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50"
                              onClick={() => publishMutation.mutate(v.version)}
                            >
                              <CheckCircle2 className="h-3.5 w-3.5" />
                              {t('designer.library.publish')}
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>
                            {t('designer.library.publishVersionLabel', { version: v.version })}
                          </TooltipContent>
                        </Tooltip>
                      )}
                    </div>
                  </div>
                ))}
              </div>

              <div className="mt-1 border-t border-border pt-1">
                <button
                  type="button"
                  role="menuitem"
                  className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-foreground hover:bg-overlay-hover active:bg-overlay-active"
                  onClick={() => {
                    setShowNewVersion(true)
                    setOpen(false)
                  }}
                >
                  <FilePlus className="h-4 w-4" />
                  {t('designer.library.newVersion')}
                </button>
                <button
                  type="button"
                  role="menuitem"
                  disabled={duplicateMutation.isPending}
                  className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-foreground hover:bg-overlay-hover active:bg-overlay-active disabled:cursor-not-allowed disabled:opacity-50"
                  onClick={() => duplicateMutation.mutate()}
                >
                  <Copy className="h-4 w-4" />
                  {t('designer.library.duplicate')}
                </button>
              </div>
            </div>
          </>,
          document.body,
        )}

      {previewVersion !== null && (
        <ComponentPreviewModal
          open={previewVersion !== null}
          onClose={() => setPreviewVersion(null)}
          designerId={row.designerId}
          rootElement={(previewSchemaQuery.data as ComponentSchemaDto | undefined)?.rootElement ?? null}
          displayName={row.displayName}
        />
      )}

      {authFilterVersion !== null && (
        <AuthFilterDialog
          designerId={row.designerId}
          version={authFilterVersion}
          onClose={() => setAuthFilterVersion(null)}
          qc={qc}
        />
      )}

      {datasetVersion !== null && (
        <DatasetDialog
          designerId={row.designerId}
          version={datasetVersion}
          onClose={() => setDatasetVersion(null)}
          qc={qc}
        />
      )}

      {showNewVersion && (
        <Dialog open={showNewVersion} onOpenChange={setShowNewVersion}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{t('designer.library.newVersion')}</DialogTitle>
            </DialogHeader>
            <p className="text-sm text-muted-foreground">
              {t('designer.library.newVersionLabel', {
                version: row.latestVersion + 1,
              })}
            </p>
            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                onClick={() => setShowNewVersion(false)}
              >
                {t('common.cancel')}
              </Button>
              <Button
                type="button"
                onClick={() => newVersionMutation.mutate()}
                disabled={newVersionMutation.isPending}
              >
                {t('common.save')}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </div>
  )
}

const createSchemaSchema = z.object({
  designerId: z
    .string()
    .trim()
    .min(1)
    .max(63)
    .regex(
      /^[a-z_][a-z0-9_]{0,62}$/,
      'Use lowercase letters, digits, and underscores. Must start with a letter or underscore. Max 63 characters.',
    ),
  displayName: z.string().trim().min(1).max(200),
  // FR-54 AC-9 — required, no default. The user must explicitly choose a mode
  // before the form is valid (and the Create button enabled).
  mode: z.enum(['CRUD', 'VIEW'], { message: 'Select a mode.' }),
})
type CreateSchemaFormValues = z.infer<typeof createSchemaSchema>

function CreateSchemaDialog({
  open,
  onOpenChange,
  qc,
}: {
  open: boolean
  onOpenChange: (next: boolean) => void
  qc: QueryClient
}) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/designer/library' })
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting, isValid },
  } = useForm<CreateSchemaFormValues>({
    resolver: zodResolver(createSchemaSchema),
    mode: 'onChange',
  })

  const createMutation = useMutation({
    mutationFn: (body: CreateSchemaFormValues) => designerApi.createSchema(body),
    onSuccess: (data) => {
      void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
      toast.success(t('designer.library.createSuccess'))
      reset()
      onOpenChange(false)
      // Open the freshly created designer immediately so the author can start
      // building. Defers to TanStack Router's typed navigate.
      void navigate({ to: '/designer/$designerId', params: { designerId: data.designerId } })
    },
  })

  const onSubmit = async (values: CreateSchemaFormValues) => {
    try {
      await createMutation.mutateAsync(values)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setError('designerId', { type: 'server', message: t('designer.library.designerIdConflict') })
        return
      }
      setError('root', { message: t('designer.library.createError') })
    }
  }

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) reset(); onOpenChange(next) }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('designer.library.createButton')}</DialogTitle>
        </DialogHeader>
        <form
          onSubmit={(e) => {
            void handleSubmit(onSubmit)(e)
          }}
          className="flex flex-col gap-3"
          aria-label={t('designer.library.createButton')}
        >
          <div className="flex flex-col gap-1">
            <label htmlFor="designerId" className="text-sm font-medium text-foreground">
              {t('designer.library.nameLabel')}
            </label>
            <Input id="designerId" autoComplete="off" {...register('designerId')} />
            {errors.designerId && (
              <span role="alert" className="text-xs text-destructive">
                {errors.designerId.message}
              </span>
            )}
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor="displayName" className="text-sm font-medium text-foreground">
              {t('designer.library.displayNameLabel')}
            </label>
            <Input id="displayName" autoComplete="off" {...register('displayName')} />
            {errors.displayName && (
              <span role="alert" className="text-xs text-destructive">
                {errors.displayName.message}
              </span>
            )}
          </div>
          <fieldset className="flex flex-col gap-1.5">
            <legend className="text-sm font-medium text-foreground">
              {t('designer.library.modeLabel')}
            </legend>
            <label htmlFor="mode-crud" className="flex items-start gap-2 text-sm">
              <input
                id="mode-crud"
                type="radio"
                value="CRUD"
                className="mt-0.5"
                {...register('mode')}
              />
              <span>
                <span className="font-medium">{t('designer.library.modeCRUD')}</span>
                {' — '}
                {t('designer.library.modeCRUDDesc')}
              </span>
            </label>
            <label htmlFor="mode-view" className="flex items-start gap-2 text-sm">
              <input
                id="mode-view"
                type="radio"
                value="VIEW"
                className="mt-0.5"
                {...register('mode')}
              />
              <span>
                <span className="font-medium">{t('designer.library.modeVIEW')}</span>
                {' — '}
                {t('designer.library.modeVIEWDesc')}
              </span>
            </label>
            {errors.mode && (
              <span role="alert" className="text-xs text-destructive">
                {errors.mode.message}
              </span>
            )}
          </fieldset>
          {errors.root && (
            <div role="alert" className="text-sm text-destructive">
              {errors.root.message}
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" disabled={isSubmitting || !isValid}>
              {t('common.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function DesignerLibraryPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/designer/library' })
  const qc = useQueryClient()
  const { page, pageSize, sort, q, status } = Route.useSearch()
  const [createOpen, setCreateOpen] = useState(false)

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['designer', 'list', page, pageSize, sort, q, status],
    queryFn: () => designerApi.listSchemas(page, pageSize, { sort, search: q, status }),
    staleTime: 30_000,
  })

  const items = data?.data ?? []
  const totalPages = Math.max(1, data?.totalPages ?? 1)
  const hasFilters = !!q || !!status
  const showEmpty = !isLoading && !isError && items.length === 0
  const errorMessage = error instanceof Error ? error.message : null

  // navigate() is stable from useNavigate; useCallback keeps these handlers
  // referentially stable so SearchBox's debounce effect isn't reset each render.
  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  const setSort = useCallback(
    (next: string | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, sort: next, page: 1 }) }),
    [navigate],
  )
  const setStatus = useCallback(
    (next: ComponentSchemaListItem['status'] | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, status: next, page: 1 }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )
  const clearFilters = useCallback(
    () => void navigate({ search: (prev) => ({ ...prev, q: undefined, status: undefined, page: 1 }) }),
    [navigate],
  )

  return (
    <div className="p-8 space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <Library className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('designer.library.title')}</h1>
          </div>
          <p className="text-muted-foreground text-sm mt-1 ml-0 sm:ml-12">
            {t('designer.library.subtitle')}
          </p>
        </div>
        <Button onClick={() => setCreateOpen(true)}>
          {t('designer.library.createButton')}
        </Button>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <SearchBox
          value={q ?? ''}
          onChange={setSearch}
          placeholder={t('designer.library.searchPlaceholder')}
        />
        <div className="flex items-center gap-2">
          <Select
            value={status ?? 'all'}
            onValueChange={(v) =>
              setStatus(v === 'all' ? undefined : (v as ComponentSchemaListItem['status']))
            }
          >
            <SelectTrigger
              className="h-9 w-40"
              aria-label={t('designer.library.filterStatusLabel')}
            >
              <SelectValue placeholder={t('designer.library.filterStatusAll')} />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t('designer.library.filterStatusAll')}</SelectItem>
              <SelectItem value="Draft">{t('designer.library.statusDraft')}</SelectItem>
              <SelectItem value="Published">{t('designer.library.statusPublished')}</SelectItem>
              <SelectItem value="Archived">{t('designer.library.statusArchived')}</SelectItem>
            </SelectContent>
          </Select>
          {hasFilters && (
            <Button variant="ghost" size="sm" onClick={clearFilters}>
              {t('designer.library.clearFilters')}
            </Button>
          )}
        </div>
      </div>

      {isError && (
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('designer.library.loadError')}
          {errorMessage ? `: ${errorMessage}` : null}
        </div>
      )}

      <div className="rounded-xl border border-border bg-card shadow-sm overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-5 py-3">
                  <SortHeader colKey="designerId" label={t('designer.library.colDesignerId')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="displayName" label={t('designer.library.colDisplayName')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="status" label={t('designer.library.colStatus')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">{t('designer.library.colMode')}</th>
                <th className="px-5 py-3">
                  <SortHeader colKey="latestVersion" label={t('designer.library.colVersion')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="updatedAt" label={t('designer.library.colUpdated')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="creator" label={t('designer.library.colCreator')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3 text-right" aria-label={t('designer.renderer.actionsHeader')} />
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {isLoading ? (
                <TableSkeleton cols={8} />
              ) : showEmpty ? (
                <tr>
                  <td colSpan={8} className="px-5 py-16 text-center">
                    <div className="flex flex-col items-center gap-3">
                      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                        <FolderOpen className="h-6 w-6" />
                      </div>
                      {hasFilters ? (
                        <>
                          <p className="font-semibold text-foreground">{t('designer.library.noMatches')}</p>
                          <Button variant="outline" onClick={clearFilters}>
                            {t('designer.library.clearFilters')}
                          </Button>
                        </>
                      ) : (
                        <>
                          <p className="font-semibold text-foreground">{t('designer.library.noSchemas')}</p>
                          <Button onClick={() => setCreateOpen(true)}>
                            {t('designer.library.createButton')}
                          </Button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ) : (
                items.map((row) => (
                  <tr
                    key={row.designerId}
                    className={cn('hover:bg-overlay-hover', row.status === 'Archived' && 'opacity-60')}
                  >
                    <td className="px-5 py-4 font-mono text-xs text-foreground">
                      <Link
                        to="/designer/$designerId"
                        params={{ designerId: row.designerId }}
                        className="text-primary hover:underline"
                      >
                        {row.designerId}
                      </Link>
                    </td>
                    <td className="px-5 py-4">
                      <span className={cn('font-medium', row.status === 'Archived' && 'line-through')}>
                        {row.displayName}
                      </span>
                    </td>
                    <td className="px-5 py-4">
                      <StatusChip status={row.status} />
                    </td>
                    <td className="px-5 py-4">
                      <ModeBadge mode={row.mode} />
                    </td>
                    <td className="px-5 py-4 text-muted-foreground">
                      <VersionFlyout
                        designerId={row.designerId}
                        latestVersion={row.latestVersion}
                      />
                    </td>
                    <td className="px-5 py-4 text-muted-foreground">{formatRelative(row.updatedAt)}</td>
                    <td className="px-5 py-4 text-muted-foreground">{row.creatorDisplayName ?? '—'}</td>
                    <td className="px-5 py-4 text-right">
                      <RowMenu row={row} qc={qc} />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {!isLoading && !isError && items.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page - 1)}
            disabled={page <= 1}
          >
            {t('designer.library.previousPage')}
          </Button>
          <span>
            {t('designer.library.pageIndicator', { page, totalPages })}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page + 1)}
            disabled={page >= totalPages}
          >
            {t('designer.library.nextPage')}
          </Button>
        </nav>
      )}

      <CreateSchemaDialog open={createOpen} onOpenChange={setCreateOpen} qc={qc} />
    </div>
  )
}
