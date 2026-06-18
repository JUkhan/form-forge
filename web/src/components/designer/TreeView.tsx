import { useCallback, useEffect, useId, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ChevronDown, ChevronRight, ChevronsUpDown, Loader2, Pencil, Plus, Search, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { getFieldKey, type DesignerElement } from '@/types/designer'
import {
  listTreeNodes,
  createTreeNode,
  listTreeDescendantIds,
  listTreeAncestorIds,
  type TreeNode,
  type TreeDatasetSource,
} from '@/features/data-entry/treeApi'
import { updateRecord } from '@/features/data-entry/updateRecordApi'
import { deleteRecord } from '@/features/data-entry/deleteRecordApi'
import { getRecord } from '@/features/data-entry/recordApi'
import ElementRenderer, { type InteractiveFormProps } from './ElementRenderer'
import RepeaterRowDrawer from './RepeaterRowDrawer'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'

const NOOP_CHANGE = () => {}
const DEFAULT_PAGE_SIZE = 25
// The dropdown trigger lists selected node names up to this count; beyond it (or when a
// name isn't known yet) it falls back to an "N selected" summary. Also caps how many
// labels are fetched to seed the trigger on edit.
const MAX_LABEL_DISPLAY = 3

function toRowVersion(v: unknown): number | undefined {
  const n = Number(v)
  return Number.isFinite(n) && n > 0 ? n : undefined
}

// Selection state shared across the whole tree (view mode only). `mode` mirrors the
// Is-multi-select property; `none` means the tree is in mutate mode (no selection).
interface TreeSelection {
  mode: 'none' | 'single' | 'multi'
  selected: Set<string>
  toggle: (id: string) => void
  // Bulk add/remove (used by "All select" cascade). Recomputes the bound value once.
  setMany: (ids: string[], select: boolean) => void
  // "All select": checking a node cascades to its whole subtree (multi mode only).
  selectAll: boolean
  radioName: string
  // Ids of nodes that are an ANCESTOR of a (seed) selected node. Lets a collapsed row
  // show a "selection inside" marker and — in single mode — auto-expand the path to it.
  // Computed once from the value seeded on mount (see note in TreeViewInteractive).
  ancestorIds: Set<string>
  // Single-select only: auto-expand the path to the seeded selection on load.
  autoExpand: boolean
  // The node-record field used as a human label (first template field). '' = none.
  labelKey: string
  // Records a node's display label as it's selected, so the dropdown trigger can show
  // names instead of a count. Captured from the row at toggle time (and seeded on edit).
  registerLabel: (id: string, label: string) => void
}

// Per-node action capabilities, derived once from the TreeView properties.
interface TreeMode {
  canCreate: boolean
  canEdit: boolean
  canDelete: boolean
  mutate: boolean
}

export default function TreeViewRenderer({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  if (!interactive) return <TreeViewPreview element={element} />
  return <TreeViewInteractive element={element} interactiveProps={interactiveProps} />
}

// ---- Canvas preview (non-interactive) ----------------------------------------

function TreeViewPreview({ element }: { element: DesignerElement }) {
  const { t } = useTranslation()
  const p = element.properties
  const label = String(p.label ?? '').trim()
  const rowDesignerId = String(p.rowDesignerId ?? '').trim()
  const templateChildren = element.children ?? []
  const viewMode = p.viewMode === true
  const mutate = !viewMode && (p.canCreate === true || p.canEdit === true || p.canDelete === true)
  const multi = !viewMode && p.isMultiSelect === true && !mutate
  const single = !viewMode && !mutate && !multi
  const isDropdownUi = p.isDropdownUi === true

  // Dropdown UI: show a closed trigger mock so the designer reflects the compact form.
  if (isDropdownUi) {
    return (
      <div className="flex w-full flex-col gap-1" data-element-id={element.id}>
        {label !== '' && <span className="text-xs font-medium text-foreground">{label}</span>}
        <div
          className={cn(
            'flex h-9 w-full items-center justify-between gap-2 rounded-lg border border-field-border bg-field px-3 py-2 text-sm text-placeholder',
            typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
          )}
        >
          <span className="truncate">{t('designer.treeView.dropdownPlaceholder')}</span>
          <ChevronsUpDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
        </div>
      </div>
    )
  }

  return (
    <div
      className={cn(
        'rounded border border-border',
        typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
      )}
      data-element-id={element.id}
    >
      {label !== '' && (
        <div className="border-b border-border bg-muted px-3 py-2 text-xs font-medium text-foreground">
          {label}
        </div>
      )}
      <ul className="py-1">
        {[0, 1].map((depth) => (
          <li
            key={depth}
            className="flex items-center gap-2 px-3 py-1.5"
            style={{ paddingLeft: `${12 + depth * 18}px` }}
          >
            {multi && <span className="h-3.5 w-3.5 shrink-0 rounded-sm border border-border" aria-hidden />}
            {single && <span className="h-3.5 w-3.5 shrink-0 rounded-full border border-border" aria-hidden />}
            <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground/60" aria-hidden />
            <div className="flex flex-1 flex-col gap-1">
              {templateChildren.length === 0 ? (
                <span className="text-xs text-muted-foreground">
                  {t('designer.renderer.configureRowTemplate')}
                </span>
              ) : (
                templateChildren.map((c) => <ElementRenderer key={c.id} element={c} interactive={false} />)
              )}
            </div>
            {!viewMode && p.canCreate === true && <Plus className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />}
            {!viewMode && p.canEdit === true && <Pencil className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />}
            {!viewMode && p.canDelete === true && <Trash2 className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />}
          </li>
        ))}
      </ul>
      <div className="border-t border-border px-3 py-1.5 text-[10px] text-muted-foreground">
        {t('designer.renderer.rowFormStatus', {
          designer: rowDesignerId === '' ? t('designer.renderer.rowFormStatusNotSet') : rowDesignerId,
          version: toRowVersion(p.rowVersion) || t('designer.renderer.rowFormStatusVersionLatest'),
        })}
      </div>
    </div>
  )
}

// ---- Interactive runtime tree ------------------------------------------------

function TreeViewInteractive({
  element,
  interactiveProps,
}: {
  element: DesignerElement
  interactiveProps?: InteractiveFormProps
}) {
  const { t } = useTranslation()
  const p = element.properties
  const label = String(p.label ?? '').trim()
  const rowDesignerId = String(p.rowDesignerId ?? '').trim()
  const rowVersion = toRowVersion(p.rowVersion)
  const pageSize = toRowVersion(p.pageSize) ?? DEFAULT_PAGE_SIZE
  const authFilterColumn =
    typeof p.authFilterColumn === 'string' && p.authFilterColumn.trim() !== ''
      ? p.authFilterColumn.trim()
      : undefined
  // Dataset-backed tree: active only when a dataset and BOTH column mappings are set.
  // The levels then read from the dataset VIEW; mutations still write the row form's table.
  const datasetSource = useMemo<TreeDatasetSource | undefined>(() => {
    const datasetId = String(p.optionsDatasetId ?? '').trim()
    const keyField = String(p.datasetKeyField ?? '').trim()
    const parentField = String(p.datasetParentField ?? '').trim()
    return datasetId !== '' && keyField !== '' && parentField !== ''
      ? { datasetId, keyField, parentField }
      : undefined
  }, [p.optionsDatasetId, p.datasetKeyField, p.datasetParentField])
  const bindTo = getFieldKey(p)
  const onChange = interactiveProps?.onChange
  const radioName = useId()

  // `viewMode` is read-only: it overrides BOTH mutation and selection.
  const viewMode = p.viewMode === true

  const mode: TreeMode = useMemo(() => {
    const canCreate = !viewMode && p.canCreate === true
    const canEdit = !viewMode && p.canEdit === true
    const canDelete = !viewMode && p.canDelete === true
    return { canCreate, canEdit, canDelete, mutate: canCreate || canEdit || canDelete }
  }, [viewMode, p.canCreate, p.canEdit, p.canDelete])

  // Selection (only when NOT view mode and NOT mutate). CRUD and multi-select are
  // mutually exclusive. Seed from the bound form value.
  const selectionMode: TreeSelection['mode'] = viewMode || mode.mutate
    ? 'none'
    : p.isMultiSelect === true
      ? 'multi'
      : 'single'

  const selectAllEnabled = selectionMode === 'multi' && p.selectAll === true

  const seedIds = useMemo(() => {
    if (selectionMode === 'none' || bindTo === '') return new Set<string>()
    const raw = interactiveProps?.formData?.[bindTo]
    if (typeof raw !== 'string' || raw.trim() === '') return new Set<string>()
    return new Set(raw.split(',').map((s) => s.trim()).filter(Boolean))
    // Seed once on mount; later edits are driven by `selected` state below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const [selected, setSelected] = useState<Set<string>>(seedIds)

  // Ancestor reveal — resolve the ancestor chain of the SEEDED selection once on mount
  // (e.g. when editing a record whose TreeView value points at deep, collapsed nodes).
  // We deliberately key off the seed, not live `selected`: it answers "where are my saved
  // selections" without an API round-trip per toggle, and freshly toggled nodes are
  // already on screen. Single-select auto-expands to the node; multi only marks ancestors.
  const [ancestorIds, setAncestorIds] = useState<Set<string>>(() => new Set())
  useEffect(() => {
    if (selectionMode === 'none' || rowDesignerId === '' || seedIds.size === 0) return
    let cancelled = false
    listTreeAncestorIds(rowDesignerId, Array.from(seedIds), authFilterColumn, datasetSource)
      .then((ids) => {
        if (!cancelled) setAncestorIds(new Set(ids))
      })
      .catch(() => {
        /* indicator is a best-effort hint; ignore failures */
      })
    return () => {
      cancelled = true
    }
    // Seed-driven, so depends only on the resolved seed + its tree source.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rowDesignerId, authFilterColumn, datasetSource])

  // The label for a node = its first template field's value. Used only by the dropdown
  // trigger to show selected names instead of a count. Row-template fields are
  // `Repeater Field` leaves that name their column via `fieldName` (NOT fieldKey/bindTo),
  // and may sit inside layout containers — so walk the subtree in document order.
  const labelKey = useMemo(() => firstTemplateFieldName(element.children ?? []), [element.children])

  const [labelById, setLabelById] = useState<Record<string, string>>({})
  const registerLabel = useCallback(
    (id: string, label: string) => {
      // Labels are only surfaced by the dropdown trigger; skip the state churn otherwise.
      if (p.isDropdownUi !== true) return
      setLabelById((prev) => (prev[id] === label ? prev : { ...prev, [id]: label }))
    },
    [p.isDropdownUi],
  )

  // Seed the trigger labels for the value loaded on mount (edit). Only the
  // provisioned-table tree can resolve a node by id; dataset-backed trees fall back to
  // capturing labels as rows are selected. Capped at MAX_LABEL_DISPLAY since beyond that
  // the trigger shows a count, not names.
  useEffect(() => {
    if (p.isDropdownUi !== true || selectionMode === 'none') return
    if (labelKey === '' || datasetSource || rowDesignerId === '') return
    const ids = Array.from(seedIds)
    if (ids.length === 0 || ids.length > MAX_LABEL_DISPLAY) return
    let cancelled = false
    Promise.allSettled(ids.map((rid) => getRecord(rowDesignerId, rid))).then((results) => {
      if (cancelled) return
      const next: Record<string, string> = {}
      results.forEach((r, i) => {
        if (r.status === 'fulfilled') {
          const raw = (r.value as Record<string, unknown>)[labelKey]
          if (raw != null && String(raw).trim() !== '') next[ids[i]] = String(raw).trim()
        }
      })
      if (Object.keys(next).length > 0) setLabelById((prev) => ({ ...next, ...prev }))
    })
    return () => {
      cancelled = true
    }
    // Seed-driven (same rationale as the ancestor effect above).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rowDesignerId, labelKey, datasetSource])

  const toggle = useCallback(
    (id: string) => {
      setSelected((prev) => {
        const next = new Set(prev)
        if (selectionMode === 'single') {
          next.clear()
          if (!prev.has(id)) next.add(id)
        } else if (next.has(id)) {
          next.delete(id)
        } else {
          next.add(id)
        }
        if (bindTo !== '' && onChange) onChange(bindTo, Array.from(next).join(','))
        return next
      })
    },
    [selectionMode, bindTo, onChange],
  )

  const setMany = useCallback(
    (ids: string[], select: boolean) => {
      setSelected((prev) => {
        const next = new Set(prev)
        if (select) for (const i of ids) next.add(i)
        else for (const i of ids) next.delete(i)
        if (bindTo !== '' && onChange) onChange(bindTo, Array.from(next).join(','))
        return next
      })
    },
    [bindTo, onChange],
  )

  const selection: TreeSelection = {
    mode: selectionMode,
    selected,
    toggle,
    setMany,
    selectAll: selectAllEnabled,
    radioName,
    ancestorIds,
    autoExpand: selectionMode === 'single',
    labelKey,
    registerLabel,
  }

  // Root-level reload token (bumped after a root-level add).
  const [rootReload, setRootReload] = useState(0)
  const [banner, setBanner] = useState<string | null>(null)
  const [rootAddOpen, setRootAddOpen] = useState(false)
  const [dropdownOpen, setDropdownOpen] = useState(false)
  // Once opened, keep the popover's tree subtree mounted (see forceMount below) so the
  // fetched levels, expansion and scroll survive close/reopen instead of refetching.
  const [dropdownOpened, setDropdownOpened] = useState(false)

  const notifyError = useCallback((message: string) => setBanner(message), [])

  const notConfigured = rowDesignerId === ''
  const isDropdownUi = p.isDropdownUi === true

  // Shared inner content: error banner, the tree (or "not configured" notice), and the
  // root-level add button. Rendered inline (normal) or inside the dropdown popover.
  const treeBody = (
    <>
      {banner && (
        <div className="border-b border-destructive/40 bg-destructive/10 px-3 py-1.5 text-xs text-destructive">
          {banner}
        </div>
      )}
      {notConfigured ? (
        <p className="px-3 py-4 text-center text-xs text-muted-foreground">
          {t('designer.renderer.rowFormStatusNotSet')}
        </p>
      ) : (
        <SearchableLevel
          designerId={rowDesignerId}
          rowVersion={rowVersion}
          parentId={null}
          depth={0}
          element={element}
          mode={mode}
          selection={selection}
          pageSize={pageSize}
          authFilterColumn={authFilterColumn}
          datasetSource={datasetSource}
          reloadSignal={rootReload}
          onError={notifyError}
        />
      )}
      {mode.canCreate && !notConfigured && (
        <div className="border-t border-border px-3 py-2">
          <button
            type="button"
            onClick={() => setRootAddOpen(true)}
            className="inline-flex items-center gap-1 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground transition-colors hover:bg-primary-hover active:bg-primary-active"
          >
            <Plus className="h-3.5 w-3.5" />
            {t('designer.treeView.addRoot')}
          </button>
        </div>
      )}
    </>
  )

  const rootAddDrawer = rootAddOpen && (
    <RepeaterRowDrawer
      designerId={rowDesignerId}
      version={rowVersion}
      mode="add"
      initialData={{}}
      hiddenFieldKey={authFilterColumn}
      onSave={(data) => {
        createTreeNode(rowDesignerId, null, data, authFilterColumn)
          .then(() => setRootReload((x) => x + 1))
          .catch((e: unknown) => notifyError(errMessage(e)))
      }}
      onClose={() => setRootAddOpen(false)}
    />
  )

  // Compact dropdown rendering: a trigger button summarising the selection that opens a
  // popover holding the full tree. Behaviour (selection / mutation) is identical inside.
  if (isDropdownUi) {
    const selectedIds = Array.from(selection.selected)
    const selectedCount = selectedIds.length
    const knownLabels = selectedIds
      .map((sid) => labelById[sid])
      .filter((l): l is string => typeof l === 'string' && l !== '')
    // Show names when we know all of them and there aren't too many; otherwise a count.
    const summary =
      selectedCount === 0
        ? t('designer.treeView.dropdownPlaceholder')
        : knownLabels.length === selectedCount && selectedCount <= MAX_LABEL_DISPLAY
          ? knownLabels.join(', ')
          : t('designer.treeView.selectedCount', { count: selectedCount })
    return (
      <div className="flex w-full flex-col gap-1">
        {label !== '' && <span className="text-xs font-medium text-foreground">{label}</span>}
        <Popover
          open={dropdownOpen}
          onOpenChange={(next) => {
            setDropdownOpen(next)
            if (next) setDropdownOpened(true)
          }}
        >
          <PopoverTrigger asChild>
            <button
              type="button"
              aria-expanded={dropdownOpen}
              className={cn(
                'flex h-9 w-full items-center justify-between gap-2 rounded-lg border border-field-border bg-field px-3 py-2 text-sm outline-none hover:bg-overlay-hover active:bg-overlay-active focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50',
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              data-element-id={element.id}
            >
              <span className={cn('truncate', selectedCount === 0 && 'text-placeholder')}>
                {summary}
              </span>
              <ChevronsUpDown className="h-4 w-4 shrink-0 text-muted-foreground" />
            </button>
          </PopoverTrigger>
          <PopoverContent
            // forceMount (after first open) keeps the tree mounted while closed — hidden
            // via data-closed:hidden — so reopening reuses the already-fetched levels.
            forceMount={dropdownOpened ? true : undefined}
            className="max-h-80 w-(--radix-popover-trigger-width) overflow-y-auto p-0 data-closed:hidden"
            align="start"
          >
            {treeBody}
          </PopoverContent>
        </Popover>
        {rootAddDrawer}
      </div>
    )
  }

  return (
    <div className="flex w-full flex-col gap-1">
      <div
        className={cn(
          'rounded border border-border',
          typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
        )}
        data-element-id={element.id}
      >
        {label !== '' && (
          <div className="border-b border-border bg-muted px-3 py-2 text-xs font-medium text-foreground">
            {label}
          </div>
        )}
        {treeBody}
      </div>
      {rootAddDrawer}
    </div>
  )
}

// One level of the tree: an optional search box + the node list + a "load more"
// pager. The search box and pager appear only when the level has more than one
// page (or a search is active) — see the TreeView spec.
function SearchableLevel({
  designerId,
  rowVersion,
  parentId,
  depth,
  element,
  mode,
  selection,
  pageSize,
  authFilterColumn,
  datasetSource,
  reloadSignal,
  onError,
}: {
  designerId: string
  rowVersion: number | undefined
  parentId: string | null
  depth: number
  element: DesignerElement
  mode: TreeMode
  selection: TreeSelection
  pageSize: number
  authFilterColumn: string | undefined
  datasetSource: TreeDatasetSource | undefined
  reloadSignal: number
  onError: (message: string) => void
}) {
  const { t } = useTranslation()
  const [rows, setRows] = useState<TreeNode[]>([])
  const [page, setPage] = useState(1)
  const [hasNext, setHasNext] = useState(false)
  const [everHadNext, setEverHadNext] = useState(false)
  const [loading, setLoading] = useState(false)
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')

  // Debounce the search box → committed `search` (resets to page 1 via the effect).
  useEffect(() => {
    const id = window.setTimeout(() => setSearch(searchInput.trim()), 300)
    return () => window.clearTimeout(id)
  }, [searchInput])

  const fetchPage = useCallback(
    async (targetPage: number, replace: boolean) => {
      setLoading(true)
      try {
        const res = await listTreeNodes(designerId, {
          parentId,
          page: targetPage,
          pageSize,
          search,
          authFilterColumn,
          dataset: datasetSource,
        })
        setRows((prev) => (replace ? res.rows : [...prev, ...res.rows]))
        setPage(targetPage)
        setHasNext(res.hasNextPage)
        if (res.hasNextPage) setEverHadNext(true)
      } catch (e: unknown) {
        onError(errMessage(e))
      } finally {
        setLoading(false)
      }
    },
    [designerId, parentId, pageSize, search, authFilterColumn, datasetSource, onError],
  )

  // (Re)load page 1 whenever the committed search or the reload signal changes.
  useEffect(() => {
    void fetchPage(1, true)
    // fetchPage is stable per (designerId,parentId,pageSize,search); reloadSignal forces refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, reloadSignal, designerId, parentId, pageSize])

  const reloadThisLevel = useCallback(() => {
    void fetchPage(1, true)
  }, [fetchPage])

  const showSearch = everHadNext || search !== ''

  return (
    <div className="flex flex-col">
      {showSearch && (
        <div
          className="flex items-center gap-1.5 px-3 py-1.5"
          style={{ paddingLeft: `${12 + depth * 18}px` }}
        >
          <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden />
          <input
            type="text"
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder={t('designer.treeView.searchPlaceholder')}
            className="h-7 w-full rounded border border-field-border bg-field px-2 text-xs text-foreground placeholder:text-placeholder"
          />
        </div>
      )}
      <ul>
        {rows.length === 0 && !loading ? (
          <li
            className="px-3 py-2 text-xs text-muted-foreground"
            style={{ paddingLeft: `${12 + depth * 18}px` }}
          >
            {search !== '' ? t('designer.treeView.noMatches') : t('designer.renderer.noRowsYet')}
          </li>
        ) : (
          rows.map((node) => (
            <TreeNodeRow
              key={String(node.id)}
              node={node}
              designerId={designerId}
              rowVersion={rowVersion}
              depth={depth}
              element={element}
              mode={mode}
              selection={selection}
              pageSize={pageSize}
              authFilterColumn={authFilterColumn}
              datasetSource={datasetSource}
              onSelfChanged={reloadThisLevel}
              onError={onError}
            />
          ))
        )}
      </ul>
      {(hasNext || loading) && (
        <div className="px-3 py-1.5" style={{ paddingLeft: `${12 + depth * 18}px` }}>
          <button
            type="button"
            disabled={loading || !hasNext}
            onClick={() => void fetchPage(page + 1, false)}
            className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-overlay-hover disabled:opacity-50"
          >
            {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
            {loading ? t('common.loading') : t('designer.treeView.loadMore')}
          </button>
        </div>
      )}
    </div>
  )
}

function TreeNodeRow({
  node,
  designerId,
  rowVersion,
  depth,
  element,
  mode,
  selection,
  pageSize,
  authFilterColumn,
  datasetSource,
  onSelfChanged,
  onError,
}: {
  node: TreeNode
  designerId: string
  rowVersion: number | undefined
  depth: number
  element: DesignerElement
  mode: TreeMode
  selection: TreeSelection
  pageSize: number
  authFilterColumn: string | undefined
  datasetSource: TreeDatasetSource | undefined
  onSelfChanged: () => void
  onError: (message: string) => void
}) {
  const { t } = useTranslation()
  const id = String(node.id)
  const hasChildren = node._has_children === true
  const templateChildren = element.children ?? []

  // Tri-state expand: null = follow the auto-expand default, true/false = user override.
  // Deriving `expanded` (rather than forcing it via an effect) avoids a cascading render
  // when the ancestor set resolves asynchronously, yet still lets the user collapse a
  // branch that was auto-expanded to reveal a selection.
  const [userExpanded, setUserExpanded] = useState<boolean | null>(null)
  const [childReload, setChildReload] = useState(0)
  const [drawer, setDrawer] = useState<null | { mode: 'add' | 'edit' }>(null)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)

  const rowFormData = useMemo<Record<string, unknown>>(() => ({ ...node }), [node])

  // This node's human label (first template field), captured on select so the dropdown
  // trigger can show names. No-op when there's no label field.
  const captureLabel = useCallback(() => {
    if (selection.labelKey === '') return
    const raw = (node as Record<string, unknown>)[selection.labelKey]
    if (raw != null && String(raw).trim() !== '') selection.registerLabel(id, String(raw).trim())
  }, [node, id, selection])

  const confirmDelete = useCallback(() => {
    setDeleting(true)
    deleteRecord(designerId, id, authFilterColumn)
      .then(() => {
        setDeleteOpen(false)
        onSelfChanged()
      })
      .catch((e: unknown) => {
        setDeleteOpen(false)
        onError(errMessage(e))
      })
      .finally(() => setDeleting(false))
  }, [designerId, id, authFilterColumn, onSelfChanged, onError])

  const isSelected = selection.selected.has(id)
  // This node sits on the path to a (seed) selection but isn't itself selected.
  const hasSelectedDescendant = !isSelected && selection.ancestorIds.has(id)

  // Single-select reveal: default-open the path to the seeded selection. As the ancestor
  // set resolves (async) this flips true and cascades — each revealed ancestor renders its
  // children, whose rows in turn default-open down to the selected node. A user override
  // (collapse/expand) wins over the default.
  const expanded = userExpanded ?? (selection.autoExpand && hasSelectedDescendant)

  // "All select": checking a node selects its whole subtree. Descendant ids are
  // fetched recursively (covers un-expanded branches); otherwise toggle just this node.
  const handleMultiToggle = useCallback(() => {
    captureLabel()
    if (selection.selectAll && hasChildren) {
      const willSelect = !selection.selected.has(id)
      listTreeDescendantIds(designerId, id, authFilterColumn, datasetSource)
        .then((desc) => selection.setMany([id, ...desc], willSelect))
        .catch((e: unknown) => onError(errMessage(e)))
    } else {
      selection.toggle(id)
    }
  }, [captureLabel, selection, hasChildren, id, designerId, authFilterColumn, datasetSource, onError])

  return (
    <li>
      <div
        className={cn(
          'flex items-center gap-2 px-3 py-1.5 hover:bg-overlay-hover',
          isSelected && 'bg-primary/10',
        )}
        style={{ paddingLeft: `${12 + depth * 18}px` }}
      >
        {selection.mode === 'multi' && (
          <input
            type="checkbox"
            checked={isSelected}
            onChange={handleMultiToggle}
            aria-label={t('designer.treeView.selectNode')}
            className="h-3.5 w-3.5 shrink-0 cursor-pointer"
          />
        )}
        {selection.mode === 'single' && (
          <input
            type="radio"
            name={selection.radioName}
            checked={isSelected}
            onChange={() => {
              captureLabel()
              selection.toggle(id)
            }}
            aria-label={t('designer.treeView.selectNode')}
            className="h-3.5 w-3.5 shrink-0 cursor-pointer"
          />
        )}
        {hasSelectedDescendant && (
          <span
            className="h-1.5 w-1.5 shrink-0 rounded-full bg-primary"
            title={t('designer.treeView.containsSelection')}
            aria-label={t('designer.treeView.containsSelection')}
          />
        )}
        {hasChildren ? (
          <button
            type="button"
            onClick={() => setUserExpanded(!expanded)}
            aria-label={expanded ? t('designer.treeView.collapse') : t('designer.treeView.expand')}
            className="shrink-0 rounded p-0.5 text-muted-foreground hover:text-foreground"
          >
            {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
          </button>
        ) : (
          <span className="h-5 w-5 shrink-0" aria-hidden />
        )}
        <div className="flex flex-1 flex-col gap-1">
          {templateChildren.length === 0 ? (
            <span className="text-xs text-muted-foreground">{id}</span>
          ) : (
            templateChildren.map((c) => (
              <ElementRenderer
                key={c.id}
                element={c}
                interactive={false}
                interactiveProps={{
                  formData: rowFormData,
                  onChange: NOOP_CHANGE,
                  onPrimaryButtonClick: NOOP_CHANGE,
                }}
              />
            ))
          )}
        </div>
        {mode.canCreate && (
          <button
            type="button"
            onClick={() => setDrawer({ mode: 'add' })}
            aria-label={t('designer.treeView.addChild')}
            className="shrink-0 rounded p-1 text-muted-foreground hover:bg-overlay-hover hover:text-primary"
          >
            <Plus className="h-4 w-4" />
          </button>
        )}
        {mode.canEdit && (
          <button
            type="button"
            onClick={() => setDrawer({ mode: 'edit' })}
            aria-label={t('designer.treeView.editNode')}
            className="shrink-0 rounded p-1 text-muted-foreground hover:bg-overlay-hover hover:text-primary"
          >
            <Pencil className="h-4 w-4" />
          </button>
        )}
        {mode.canDelete && (
          <button
            type="button"
            onClick={() => setDeleteOpen(true)}
            aria-label={t('designer.treeView.deleteNode')}
            className="shrink-0 rounded p-1 text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
          >
            <Trash2 className="h-4 w-4" />
          </button>
        )}
      </div>
      {expanded && hasChildren && (
        <SearchableLevel
          designerId={designerId}
          rowVersion={rowVersion}
          parentId={id}
          depth={depth + 1}
          element={element}
          mode={mode}
          selection={selection}
          pageSize={pageSize}
          authFilterColumn={authFilterColumn}
          datasetSource={datasetSource}
          reloadSignal={childReload}
          onError={onError}
        />
      )}
      {drawer && (
        <RepeaterRowDrawer
          designerId={designerId}
          version={rowVersion}
          mode={drawer.mode}
          initialData={drawer.mode === 'edit' ? rowFormData : {}}
          hiddenFieldKey={authFilterColumn}
          onSave={(data) => {
            const op =
              drawer.mode === 'edit'
                ? updateRecord(designerId, id, data, authFilterColumn)
                : createTreeNode(designerId, id, data, authFilterColumn)
            op
              .then(() => {
                if (drawer.mode === 'edit') {
                  onSelfChanged()
                } else {
                  // New child added — expand and reload this node's children.
                  setUserExpanded(true)
                  setChildReload((x) => x + 1)
                  // The node may have flipped from leaf → parent; refresh this level too.
                  onSelfChanged()
                }
              })
              .catch((e: unknown) => onError(errMessage(e)))
          }}
          onClose={() => setDrawer(null)}
        />
      )}
      <AlertDialog open={deleteOpen} onOpenChange={(open) => { if (!open) setDeleteOpen(false) }}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('designer.treeView.deleteNode')}</AlertDialogTitle>
            <AlertDialogDescription>{t('designer.treeView.confirmDelete')}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleting}>{t('designer.treeView.cancel')}</AlertDialogCancel>
            <AlertDialogAction
              disabled={deleting}
              onClick={(e) => {
                e.preventDefault()
                confirmDelete()
              }}
            >
              {t('designer.treeView.delete')}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </li>
  )
}

// Document-order search for the first `Repeater Field` leaf with a column binding,
// returning its `fieldName` (the node-record key, == DB column). Mirrors the column
// discovery that ElementRenderer's collectRepeaterFields does for the table view.
function firstTemplateFieldName(nodes: DesignerElement[]): string {
  for (const node of nodes) {
    if (node.type === 'Repeater Field') {
      const fn = node.properties.fieldName
      if (typeof fn === 'string' && fn.trim() !== '') return fn.trim()
      continue
    }
    if (node.children && node.children.length > 0) {
      const found = firstTemplateFieldName(node.children)
      if (found !== '') return found
    }
  }
  return ''
}

function errMessage(e: unknown): string {
  if (e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string') {
    return (e as { message: string }).message
  }
  return 'Request failed'
}
