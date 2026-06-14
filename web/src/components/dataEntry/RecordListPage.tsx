import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Database, Download, FileSpreadsheet, FileText, Filter, FilterX, Plus, FolderOpen } from 'lucide-react'
import { usePermission } from '@/features/auth/usePermission'
import { usePermissionsQuery, PLATFORM_ADMIN_ROLE_ID } from '@/features/auth/usePermissionsQuery'
import { useRecordList } from '@/features/data-entry/useRecordList'
import {
  useDesignerFieldKeys,
  type ReferenceFilterConfig,
  type TableColumn,
} from '@/features/data-entry/useDesignerFieldKeys'
import { downloadRecordExport, type ExportFormat } from '@/features/data-entry/exportApi'
import { compileMapExpression, type CompiledMapExpression } from '@/features/data-entry/mapExpression'
import { fetchDropdownOptions } from '@/features/data-entry/dropdownOptionsApi'
import { useRestoreRecord } from '@/features/data-entry/useRestoreRecord'
import { ApiError } from '@/lib/api/apiError'
import { Undo2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Combobox, type ComboboxOption } from '@/components/ui/combobox'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorBanner } from '@/components/shared/ErrorBanner'
import { cn } from '@/lib/utils'

// Story 6.10 — full record list: schema-driven columns, multi-column sort,
// per-column filter bar, 10/25/50 page-size picker, soft-delete row indicator,
// total count. All pagination/sort/filter state lives in the URL search params
// owned by the host route; this component is a pure display + callback
// surface. Story 6.11 swapped the inline loading text for a Skeleton table,
// the toast-on-error useEffect for an inline ErrorBanner with Retry, and the
// bare noRecords text for an empty-state CTA gated on canCreate.

type SortEntry = { key: string; dir: 'asc' | 'desc' }

const PAGE_SIZE_OPTIONS = [10, 25, 50] as const

function parseSortString(sort: string | undefined): SortEntry[] {
  if (!sort) return []
  return sort
    .split(',')
    .map((s) => s.split(':'))
    .filter(
      (parts): parts is [string, string] =>
        parts.length === 2 && (parts[1] === 'asc' || parts[1] === 'desc'),
    )
    .map(([key, dir]) => ({ key, dir: dir as 'asc' | 'desc' }))
}

function serializeSortEntries(entries: SortEntry[]): string | undefined {
  if (entries.length === 0) return undefined
  return entries.map((e) => `${e.key}:${e.dir}`).join(',')
}

function ariaSortFor(
  colKey: string,
  sortEntries: SortEntry[],
): 'ascending' | 'descending' | 'none' {
  const entry = sortEntries.find((e) => e.key === colKey)
  if (!entry) return 'none'
  return entry.dir === 'asc' ? 'ascending' : 'descending'
}

function nextSortForClick(
  colKey: string,
  shiftKey: boolean,
  current: SortEntry[],
): SortEntry[] {
  const existing = current.find((e) => e.key === colKey)
  if (!shiftKey) {
    // Single-column cycle: none → asc → desc → none.
    if (!existing) return [{ key: colKey, dir: 'asc' }]
    if (existing.dir === 'asc') return [{ key: colKey, dir: 'desc' }]
    return []
  }
  // Shift-click appends/toggles/removes within the existing multi-sort.
  if (!existing) return [...current, { key: colKey, dir: 'asc' }]
  if (existing.dir === 'asc') {
    return current.map((e) =>
      e.key === colKey ? { ...e, dir: 'desc' as const } : e,
    )
  }
  return current.filter((e) => e.key !== colKey)
}

function formatTimestamp(value: unknown): string {
  if (typeof value !== 'string' || value === '') return '—'
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? value : d.toLocaleString()
}

function formatCell(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

interface RecordListPageProps {
  designerId: string
  page: number
  pageSize: number
  sort?: string
  filter?: Record<string, string>
  showDeleted?: boolean
  onPageChange: (page: number) => void
  onPageSizeChange: (pageSize: number) => void
  onSortChange: (sort: string | undefined) => void
  onFilterChange: (filter: Record<string, string> | undefined) => void
  onShowDeletedChange?: (next: boolean) => void
}

interface SortableHeaderProps {
  colKey: string
  label: string
  sortEntries: SortEntry[]
  multi: boolean
  onSortChange: (sort: string | undefined) => void
  // When set, the label is rendered as-is (not upper-cased by the default
  // header styling) — used for humanized user-column labels like "Report
  // Title" that should keep their title-case rather than become ALL CAPS.
  normalCase?: boolean
  ariaSortAscLabel: string
  ariaSortDescLabel: string
  ariaPriorityLabelFor: (n: number) => string
}

function SortableHeader({
  colKey,
  label,
  sortEntries,
  multi,
  onSortChange,
  normalCase,
  ariaSortAscLabel,
  ariaSortDescLabel,
  ariaPriorityLabelFor,
}: SortableHeaderProps) {
  const sortIndex = sortEntries.findIndex((e) => e.key === colKey)
  const sortDir = sortIndex >= 0 ? sortEntries[sortIndex].dir : null
  const priority = multi && sortIndex >= 0 ? sortIndex + 1 : null
  return (
    <button
      type="button"
      onClick={(e) => {
        const next = nextSortForClick(colKey, e.shiftKey, sortEntries)
        onSortChange(serializeSortEntries(next))
      }}
      className="flex items-center gap-1 text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground hover:text-foreground"
    >
      <span className={normalCase ? 'normal-case tracking-normal' : ''}>{label}</span>
      {sortDir === 'asc' && <span aria-label={ariaSortAscLabel}>↑</span>}
      {sortDir === 'desc' && <span aria-label={ariaSortDescLabel}>↓</span>}
      {priority !== null && (
        <span
          aria-label={ariaPriorityLabelFor(priority)}
          className="text-xs text-muted-foreground"
        >
          {priority}
        </span>
      )}
    </button>
  )
}

interface FilterRowProps {
  columns: TableColumn[]
  // initialFilter mirrors the URL filter state. We can't `key`-remount on this
  // value because the parent re-renders with a new filter prop on every push
  // we make ourselves — remounting on the echo would steal focus from the
  // input mid-typing. Instead we sync via a ref (see lastSyncedJsonRef below).
  initialFilter: Record<string, string>
  filterPlaceholder: string
  ariaFilterLabelFor: (col: string) => string
  onFilterChange: (filter: Record<string, string> | undefined) => void
  onPageChange: (page: number) => void
  // Number of trailing empty cells to render so the filter row aligns with
  // the header (created_at + updated_at, optionally + actions when the
  // 'Show deleted' toggle is on).
  trailingColCount: number
}

// Canonical JSON form (sorted keys) lets us compare two filter maps by value
// without writing a per-field loop everywhere.
function canonicalizeFilter(f: Record<string, string>): string {
  return JSON.stringify(
    Object.fromEntries(Object.entries(f).sort(([a], [b]) => a.localeCompare(b))),
  )
}

function FilterRow({
  columns,
  initialFilter,
  filterPlaceholder,
  ariaFilterLabelFor,
  onFilterChange,
  onPageChange,
  trailingColCount,
}: FilterRowProps) {
  const [filterBuffer, setFilterBuffer] = useState<Record<string, string>>(
    initialFilter,
  )
  // Records the last filter value we know the parent has — either the mount
  // seed, the result of our own debounce push, or an external change we've
  // already absorbed. We compare incoming `initialFilter` against this:
  //   - equal → it's just the echo of our own push; do nothing (preserves
  //     focus on the input the user is typing into).
  //   - different → an external change (back/forward nav, clear button, route
  //     change) — re-seed the local buffer.
  const lastSyncedJsonRef = useRef<string>(canonicalizeFilter(initialFilter))

  // Detect external changes to `initialFilter` and sync. Skips the no-op case
  // where the prop change is the parent re-rendering with the value WE just
  // pushed (which would otherwise cause a `setState` that interrupts typing).
  useEffect(() => {
    const incomingJson = canonicalizeFilter(initialFilter)
    if (incomingJson === lastSyncedJsonRef.current) return
    lastSyncedJsonRef.current = incomingJson
    setFilterBuffer(initialFilter)
  }, [initialFilter])

  // Debounce: 300 ms after the last keystroke, push the cleaned buffer to the
  // URL and reset to page 1. The callbacks are stable references from the
  // route, so excluding them from the dep array does not cause stale closures.
  // The same window applies to boolean/date widgets too — they only change
  // on commit, so the 300 ms post-change pause is imperceptible.
  useEffect(() => {
    const id = setTimeout(() => {
      const clean = Object.fromEntries(
        Object.entries(filterBuffer).filter(([, v]) => v !== ''),
      )
      const cleanJson = canonicalizeFilter(clean)
      // Equal to last known parent value → nothing to push. Covers the mount
      // case and the post-sync case (after we just absorbed an external value).
      if (cleanJson === lastSyncedJsonRef.current) return
      lastSyncedJsonRef.current = cleanJson
      const cleanKeys = Object.keys(clean)
      onFilterChange(cleanKeys.length > 0 ? clean : undefined)
      onPageChange(1)
    }, 300)
    return () => clearTimeout(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterBuffer])

  function handleFilterInput(colKey: string, value: string) {
    setFilterBuffer((prev) =>
      value === ''
        ? Object.fromEntries(Object.entries(prev).filter(([k]) => k !== colKey))
        : { ...prev, [colKey]: value },
    )
  }

  return (
    <tr className="bg-muted/60">
      {columns.map((col) => {
        // Reference columns filter on the stored FK column (the dropdown's
        // fieldKey), not on the display-only label alias in dataKey.
        const filterKey = col.referenceFilter?.filterColumn ?? col.dataKey
        return (
          <td key={col.dataKey} className="px-3 py-1.5">
            {/* Designer-backed Dropdown: same searchable combobox the form uses,
                picking an option filters this table on the stored FK value. */}
            {col.referenceFilter ? (
              <ReferenceFilterCell
                config={col.referenceFilter}
                value={filterBuffer[filterKey] ?? ''}
                ariaLabel={ariaFilterLabelFor(filterKey)}
                onChange={(v) => handleFilterInput(filterKey, v)}
              />
            ) : col.filterKind !== undefined ? (
              <FilterCell
                kind={col.filterKind}
                value={filterBuffer[col.dataKey] ?? ''}
                placeholder={filterPlaceholder}
                ariaLabel={ariaFilterLabelFor(col.dataKey)}
                onChange={(v) => handleFilterInput(col.dataKey, v)}
              />
            ) : (
              /* Repeater count and misconfigured-reference columns are
                 display-only — render an empty cell to keep alignment. */
              null
            )}
          </td>
        )
      })}
      {Array.from({ length: trailingColCount }, (_, i) => (
        <td key={`trailing-${i}`} className="px-3 py-1.5" />
      ))}
    </tr>
  )
}

interface FilterCellProps {
  kind: TableColumn['filterKind']
  value: string
  placeholder: string
  ariaLabel: string
  onChange: (next: string) => void
}

// Renders the type-appropriate filter widget for a single column. The kind
// comes from the backend ComponentTypeMapper via useDesignerFieldKeys; the
// backend WHERE clause makes a matching dispatch (ILIKE / ::numeric / bool
// / ::date) so the value shape sent here lands in a comparison that actually
// matches rows.
function FilterCell({ kind, value, placeholder, ariaLabel, onChange }: FilterCellProps) {
  switch (kind) {
    case 'number':
      return (
        <Input
          type="number"
          inputMode="decimal"
          step="any"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          aria-label={ariaLabel}
          className="h-7 text-xs"
        />
      )
    case 'boolean':
      // Tri-state via the empty option: blank = no filter, true/false =
      // exact match on the BOOLEAN column.
      return (
        <select
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-label={ariaLabel}
          className="h-7 w-full rounded-md border border-input bg-transparent px-2 text-xs"
        >
          <option value="">—</option>
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      )
    case 'datetime':
      return (
        <Input
          type="date"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-label={ariaLabel}
          className="h-7 text-xs"
        />
      )
    case 'text':
    case 'other':
    default:
      return (
        <Input
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          aria-label={ariaLabel}
          className="h-7 text-xs"
        />
      )
  }
}

// Sentinel for the "Any" clear row — a value no real FK can collide with. Picking
// it clears the filter (onChange('')). Non-empty so cmdk renders it in server mode.
const REFERENCE_FILTER_CLEAR = ' clear'

interface ReferenceFilterCellProps {
  config: ReferenceFilterConfig
  value: string
  ariaLabel: string
  onChange: (next: string) => void
}

// Filter widget for a Designer-backed Dropdown column. Mirrors the form's
// DesignerDropdownField: a server-searched, paginated combobox over the
// referenced designer's /options endpoint. The selected option's value is the
// stored FK, so onChange feeds it straight into filter[fieldKey]=<value>; the
// backend matches it against the (UUID/TEXT) FK column with the right cast.
function ReferenceFilterCell({ config, value, ariaLabel, onChange }: ReferenceFilterCellProps) {
  const { t } = useTranslation()
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(search), 250)
    return () => window.clearTimeout(id)
  }, [search])

  const optionsQuery = useQuery({
    queryKey: [
      'dropdown-options',
      config.designerId,
      config.version,
      config.labelField,
      config.valueField,
      debouncedSearch,
    ],
    queryFn: () =>
      fetchDropdownOptions(config.designerId, {
        version: config.version,
        labelField: config.labelField,
        valueField: config.valueField,
        search: debouncedSearch || undefined,
        page: 1,
        pageSize: 50,
      }),
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  })
  const pageOptions = useMemo<ComboboxOption[]>(
    () =>
      (optionsQuery.data?.data ?? []).map((o) => ({ value: o.value, label: o.label ?? o.value })),
    [optionsQuery.data],
  )

  // Resolve the label for an active filter value whose row isn't on the current
  // page (e.g. a value seeded from the URL) so the trigger shows the label.
  const selectedInPage = value !== '' && pageOptions.some((o) => o.value === value)
  const selectedQuery = useQuery({
    queryKey: [
      'dropdown-option-label',
      config.designerId,
      config.version,
      config.labelField,
      config.valueField,
      value,
    ],
    queryFn: () =>
      fetchDropdownOptions(config.designerId, {
        version: config.version,
        labelField: config.labelField,
        valueField: config.valueField,
        page: 1,
        pageSize: 1,
        filter: { [config.valueField]: value },
      }),
    enabled: value !== '' && !selectedInPage,
    staleTime: 60_000,
  })
  const selectedLabel = selectedQuery.data?.data?.[0]?.label

  const options = useMemo<ComboboxOption[]>(() => {
    const base =
      value !== '' && !selectedInPage && selectedLabel
        ? [{ value, label: selectedLabel }, ...pageOptions]
        : pageOptions
    // Offer an explicit "Any" row to clear the filter once one is set.
    return value !== ''
      ? [{ value: REFERENCE_FILTER_CLEAR, label: t('data.entry.filterAny') }, ...base]
      : base
  }, [value, selectedInPage, selectedLabel, pageOptions, t])

  return (
    <Combobox
      value={value}
      onValueChange={(v) => onChange(v === REFERENCE_FILTER_CLEAR ? '' : v)}
      options={options}
      onSearchChange={setSearch}
      loading={optionsQuery.isLoading}
      placeholder={t('data.entry.filterPlaceholder')}
      emptyText={optionsQuery.isError ? t('data.entry.loadError') : t('data.entry.filterNoResults')}
      className="h-7 text-xs"
      aria-label={ariaLabel}
    />
  )
}

export function RecordListPage({
  designerId,
  page,
  pageSize,
  sort,
  filter,
  showDeleted = false,
  onPageChange,
  onPageSizeChange,
  onSortChange,
  onFilterChange,
  onShowDeletedChange,
}: RecordListPageProps) {
  const { t } = useTranslation()
  // `from:` so navigate() is typed against this route's source path; without
  // it the type signature rejects the navigate({ to, params }) call below.
  const navigate = useNavigate({ from: '/data/$designerId' })
  const canCreate = usePermission(designerId, 'canCreate')
  const schema = useDesignerFieldKeys(designerId)

  // Precompile each Field column's map expression once (keyed by the column's
  // dataKey), so the per-row cell render is a cheap function call rather than a
  // recompile. Declared up here (before the early returns) to keep hook order stable.
  const mapExpressions = useMemo(() => {
    const m = new Map<string, CompiledMapExpression>()
    for (const col of schema.columns) {
      if (col.mapExpression) {
        m.set(col.dataKey, compileMapExpression(col.mapExpression, col.accessorKey ?? col.dataKey))
      }
    }
    return m
  }, [schema.columns])

  // Viewing / restoring soft-deleted rows is a platform-admin-only capability —
  // mirrors the admin route guard's check (admin.tsx). Non-admins never see the
  // toggle, and a hand-crafted ?showDeleted=true in their URL has no effect
  // (effectiveShowDeleted stays false), so deleted rows can't leak into a
  // non-admin's list or export.
  const { data: permissions } = usePermissionsQuery()
  const isAdmin = permissions?.roleIds.includes(PLATFORM_ADMIN_ROLE_ID) ?? false
  const effectiveShowDeleted = isAdmin && showDeleted

  const params = { page, pageSize, sort, filter, includeDeleted: effectiveShowDeleted }
  const query = useRecordList(designerId, params)
  const canDelete = usePermission(designerId, 'canDelete')
  const canExport = usePermission(designerId, 'canExport')
  const restoreMutation = useRestoreRecord(designerId)
  // Export-in-flight flag — declared up here (before the early-return loading
  // / error branches) so the hook-call order is stable across renders.
  const [exporting, setExporting] = useState(false)
  // Filter panel visibility — collapsed by default, toggled from the header.
  // Auto-opens when the page is entered with an active filter (e.g. a shared
  // URL) so the inputs that produced the result set are visible, not hidden.
  const [showFilters, setShowFilters] = useState(
    () => !!filter && Object.keys(filter).length > 0,
  )

  const sortEntries = parseSortString(sort)
  const multi = sortEntries.length > 1

  // displayName is undefined while the schema query loads; fall back to the
  // raw designerId so the header never reads "undefined" or empty.
  const headerTitle = schema.displayName ?? designerId

  if (query.isLoading && !query.data) {
    return (
      <div className="flex h-full flex-col">
        <PageHeader designerId={designerId} title={headerTitle} canCreate={false} />
        <div className="flex-1 overflow-y-auto p-6">
          <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
            <div className="space-y-2">
              {Array.from({ length: 6 }).map((_, i) => (
                <Skeleton key={i} className="h-8 w-full" />
              ))}
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (query.isError && !query.data) {
    return (
      <div className="flex h-full flex-col">
        <PageHeader designerId={designerId} title={headerTitle} canCreate={false} />
        <div className="flex-1 overflow-y-auto p-6">
          <ErrorBanner
            error={query.error}
            onRetry={() => void query.refetch()}
          />
        </div>
      </div>
    )
  }

  const rows = query.data?.data ?? []
  const total = query.data?.total ?? 0
  const totalPages = query.data?.totalPages ?? page
  const columns = schema.isError ? [] : schema.columns
  // Distinguishes "table is genuinely empty" (no filter, show the create CTA)
  // from "the active filter matches nothing" (keep the table chrome mounted so
  // the user can see + clear what they filtered by). Without this branch the
  // FilterRow disappears the moment a typed query stops matching, which traps
  // the user with a blank page.
  const hasActiveFilter = !!filter && Object.keys(filter).length > 0

  const sortAscLabel = t('data.entry.sortAsc')
  const sortDescLabel = t('data.entry.sortDesc')
  const filterPlaceholder = t('data.entry.filterPlaceholder')
  const ariaPriorityLabelFor = (n: number) => t('data.entry.sortPriority', { n })
  const ariaFilterLabelFor = (col: string) =>
    t('data.entry.filterAriaLabel', { column: col })
  const seedFilter = filter ?? {}

  const goToCreate = () => {
    void navigate({
      to: '/data/$designerId/new',
      params: { designerId },
      // /data/$designerId/new is a child of /data/$designerId, which has a
      // validateSearch with defaults — explicit search is required at the
      // typed-navigate level.
      search: { page: 1, pageSize: 25 },
    })
  }

  const handleClearFilters = () => {
    onFilterChange(undefined)
    onPageChange(1)
  }

  const handleRestore = (recordId: string, event: React.MouseEvent) => {
    // Restore is a row action — stop the click from also triggering the
    // row's onClick (which would navigate to the detail page).
    event.stopPropagation()
    restoreMutation.mutate(recordId, {
      onSuccess: () => toast.success(t('data.entry.restoreSuccess')),
      onError: (err) => {
        const messageKey = err instanceof ApiError ? err.messageKey : 'data.entry.restoreError'
        toast.error(t(messageKey))
      },
    })
  }

  const handleExport = async (format: ExportFormat) => {
    if (exporting) return
    setExporting(true)
    try {
      // Honor the user's current filter + sort + show-deleted toggle —
      // exporting "what I see" is the universally-expected behavior.
      // Pagination is intentionally NOT forwarded; the export endpoint streams
      // the full filtered result set.
      await downloadRecordExport(designerId, format, {
        sort,
        filter,
        includeDeleted: effectiveShowDeleted,
      })
      toast.success(t('data.entry.exportSuccess', { format: format.toUpperCase() }))
    } catch (err) {
      const messageKey = err instanceof ApiError ? err.messageKey : 'errors.exportFailed'
      toast.error(t(messageKey))
    } finally {
      setExporting(false)
    }
  }

  return (
    <div className="flex h-full flex-col" aria-busy={query.isFetching}>
      <PageHeader
        designerId={designerId}
        title={headerTitle}
        canCreate={canCreate}
        onCreate={goToCreate}
        // Export buttons render only when the role grants canExport (the
        // PageHeader hides them when onExport is undefined). Platform admins
        // bypass via usePermission.
        onExport={canExport ? (format) => void handleExport(format) : undefined}
        exporting={exporting}
        showFilters={showFilters}
        onToggleFilters={() => setShowFilters((v) => !v)}
        hasActiveFilter={hasActiveFilter}
        onClearFilters={handleClearFilters}
        showDeleted={effectiveShowDeleted}
        // Only admins get the toggle — withholding the handler hides the
        // checkbox in PageHeader (it renders only when onShowDeletedChange is set).
        onShowDeletedChange={isAdmin ? onShowDeletedChange : undefined}
      />
      <div className="flex-1 overflow-y-auto p-6">
        {rows.length === 0 && !hasActiveFilter ? (
          <div className="rounded-xl border border-border bg-card py-16 shadow-sm">
            <div className="flex flex-col items-center gap-3">
              <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                <FolderOpen className="h-6 w-6" />
              </div>
              <p className="font-semibold text-foreground">{t('data.entry.noRecords')}</p>
              {canCreate && (
                <Button onClick={goToCreate}>
                  <Plus className="h-4 w-4" />
                  {t('data.entry.createFirstRecord')}
                </Button>
              )}
            </div>
          </div>
        ) : (
          <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="bg-muted text-left">
                    {columns.map((col) => (
                      <th
                        key={col.dataKey}
                        className="px-3 py-3"
                        aria-sort={col.sortable ? ariaSortFor(col.dataKey, sortEntries) : undefined}
                      >
                        {col.sortable ? (
                          <SortableHeader
                            colKey={col.dataKey}
                            label={col.header}
                            sortEntries={sortEntries}
                            multi={multi}
                            onSortChange={onSortChange}
                            normalCase
                            ariaSortAscLabel={sortAscLabel}
                            ariaSortDescLabel={sortDescLabel}
                            ariaPriorityLabelFor={ariaPriorityLabelFor}
                          />
                        ) : (
                          // Derived columns (dropdown label / repeater count) are
                          // display-only — render a plain, non-clickable header.
                          <span className="text-[11px] font-semibold tracking-wider text-muted-foreground">
                            {col.header}
                          </span>
                        )}
                      </th>
                    ))}
                    <th
                      className="px-3 py-3"
                      aria-sort={ariaSortFor('created_at', sortEntries)}
                    >
                      <SortableHeader
                        colKey="created_at"
                        label={t('data.entry.columnCreatedAt')}
                        sortEntries={sortEntries}
                        multi={multi}
                        onSortChange={onSortChange}
                        ariaSortAscLabel={sortAscLabel}
                        ariaSortDescLabel={sortDescLabel}
                        ariaPriorityLabelFor={ariaPriorityLabelFor}
                      />
                    </th>
                    <th
                      className="px-3 py-3"
                      aria-sort={ariaSortFor('updated_at', sortEntries)}
                    >
                      <SortableHeader
                        colKey="updated_at"
                        label={t('data.entry.columnUpdatedAt')}
                        sortEntries={sortEntries}
                        multi={multi}
                        onSortChange={onSortChange}
                        ariaSortAscLabel={sortAscLabel}
                        ariaSortDescLabel={sortDescLabel}
                        ariaPriorityLabelFor={ariaPriorityLabelFor}
                      />
                    </th>
                    {effectiveShowDeleted && (
                      <th className="px-3 py-3 text-right text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                        {t('data.entry.columnActions')}
                      </th>
                    )}
                  </tr>
                  {showFilters && (
                    <FilterRow
                      columns={columns}
                      initialFilter={seedFilter}
                      filterPlaceholder={filterPlaceholder}
                      ariaFilterLabelFor={ariaFilterLabelFor}
                      onFilterChange={onFilterChange}
                      onPageChange={onPageChange}
                      trailingColCount={effectiveShowDeleted ? 3 : 2}
                    />
                  )}
                </thead>
                <tbody className="divide-y divide-border">
                  {rows.length === 0 ? (
                    // Filter active but no matches — keep the chrome mounted
                    // so the user can adjust or clear filters from where they
                    // are. Colspan = user columns + created_at + updated_at
                    // (+ actions when 'Show deleted' is on).
                    <tr>
                      <td
                        colSpan={columns.length + (effectiveShowDeleted ? 3 : 2)}
                        className="px-3 py-10 text-center text-sm text-muted-foreground"
                      >
                        <div className="flex flex-col items-center gap-3">
                          <span>{t('data.entry.noRecordsMatchFilter')}</span>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            onClick={() => {
                              onFilterChange(undefined)
                              onPageChange(1)
                            }}
                          >
                            {t('data.entry.clearFilters')}
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ) : rows.map((row) => {
                    const deleted = row.isDeleted === true
                    const cellMuted = deleted ? 'text-muted-foreground line-through' : 'text-foreground'
                    // Soft-deleted badge, relocated here from the removed id
                    // column; rendered in the first data cell (or created_at
                    // when the schema exposes no user columns).
                    const deletedBadge = deleted ? (
                      <span className="mr-1.5 inline-flex items-center rounded-full bg-destructive/15 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wider text-destructive">
                        {t('data.entry.deletedBadge')}
                      </span>
                    ) : null
                    return (
                      <tr
                        key={row.id}
                        onClick={() => {
                          void navigate({
                            to: '/data/$designerId/$recordId',
                            params: { designerId, recordId: row.id },
                            search: { page: 1, pageSize: 25 },
                          })
                        }}
                        className={cn(
                          'cursor-pointer hover:bg-overlay-hover active:bg-overlay-active',
                          deleted && 'opacity-60',
                        )}
                      >
                        {columns.map((col, i) => {
                          // Field columns read the underlying fieldName via accessorKey
                          // (their dataKey is a synthetic element id) and may map the value.
                          const accessor = col.accessorKey ?? col.dataKey
                          const compiled = mapExpressions.get(col.dataKey)
                          const raw = row[accessor]
                          return (
                            <td key={col.dataKey} className={cn('px-3 py-2', cellMuted)}>
                              {i === 0 && deletedBadge}
                              {formatCell(compiled ? compiled(raw) : raw)}
                            </td>
                          )
                        })}
                        <td className={cn('whitespace-nowrap px-3 py-2 text-muted-foreground', deleted && 'line-through text-muted-foreground')}>
                          {columns.length === 0 && deletedBadge}
                          {formatTimestamp(row.createdAt)}
                        </td>
                        <td className={cn('whitespace-nowrap px-3 py-2 text-muted-foreground', deleted && 'line-through text-muted-foreground')}>
                          {formatTimestamp(row.updatedAt)}
                        </td>
                        {effectiveShowDeleted && (
                          <td className="whitespace-nowrap px-3 py-2 text-right">
                            {deleted && canDelete && (
                              <Button
                                type="button"
                                variant="outline"
                                size="sm"
                                onClick={(e) => handleRestore(row.id, e)}
                                disabled={restoreMutation.isPending}
                                aria-label={t('data.entry.restoreAria')}
                              >
                                <Undo2 className="h-3.5 w-3.5" />
                                {t('data.entry.restore')}
                              </Button>
                            )}
                          </td>
                        )}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <nav className="mt-4 flex items-center justify-end gap-3 text-sm text-muted-foreground">
          <span>{t('data.entry.totalRecords', { total })}</span>
          <label className="flex items-center gap-2">
            <span>{t('data.entry.pageSizeLabel')}</span>
            {/* Native <select> (not shadcn) because the existing test suite
                fires a `change` event on it via getByRole('combobox') — radix's
                custom trigger doesn't fire native change events. Styled to match
                the shadcn Input visual weight. */}
            <select
              value={pageSize}
              onChange={(e) => onPageSizeChange(Number(e.target.value))}
              className="h-8 rounded-lg border border-input bg-transparent px-2 py-1 text-sm transition-colors outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            >
              {PAGE_SIZE_OPTIONS.map((opt) => (
                <option key={opt} value={opt}>
                  {opt}
                </option>
              ))}
            </select>
          </label>
          <Button
            variant="outline"
            size="sm"
            onClick={() => onPageChange(Math.max(1, page - 1))}
            disabled={page === 1}
          >
            {t('data.entry.prevPage')}
          </Button>
          <span>{t('data.entry.pageInfo', { page, totalPages })}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => onPageChange(Math.min(totalPages, page + 1))}
            disabled={page >= totalPages}
          >
            {t('data.entry.nextPage')}
          </Button>
        </nav>
      </div>
    </div>
  )
}

interface PageHeaderProps {
  // `designerId` is the underlying schema identifier — kept on the title
  // attribute as a hover-tooltip so admins can still see it without
  // dominating the visible chrome with a slug.
  designerId: string
  // `title` is the human-readable schema name from ComponentSchemaDto
  // (falls back to designerId at the call site when the schema hasn't
  // loaded yet).
  title: string
  canCreate: boolean
  onCreate?: () => void
  onExport?: (format: ExportFormat) => void
  // True while an export is downloading — disables the buttons so a slow
  // export can't be triggered three times in a row.
  exporting?: boolean
  // Filter panel toggle (icon-button before the export buttons) + the Clear
  // affordance that surfaces while the panel is open.
  showFilters?: boolean
  onToggleFilters?: () => void
  hasActiveFilter?: boolean
  onClearFilters?: () => void
  showDeleted?: boolean
  onShowDeletedChange?: (next: boolean) => void
}

function PageHeader({
  designerId,
  title,
  canCreate,
  onCreate,
  onExport,
  exporting,
  showFilters,
  onToggleFilters,
  hasActiveFilter,
  onClearFilters,
  showDeleted,
  onShowDeletedChange,
}: PageHeaderProps) {
  const { t } = useTranslation()
  return (
    <div className="flex shrink-0 items-center justify-between gap-4 border-b border-border bg-card px-6 py-4">
      <div className="flex items-center gap-2.5 min-w-0">
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
          <Database className="h-5 w-5" />
        </div>
        <Tooltip>
          <TooltipTrigger asChild>
            <h1 className="truncate text-lg font-semibold tracking-tight text-foreground">
              {title}
            </h1>
          </TooltipTrigger>
          {/* Reveals the raw designerId (the db-column-style schema identifier)
              for power-users / debug. */}
          <TooltipContent>{designerId}</TooltipContent>
        </Tooltip>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        {onToggleFilters && (
          <>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  type="button"
                  variant={showFilters ? 'default' : 'outline'}
                  size="sm"
                  onClick={onToggleFilters}
                  aria-pressed={!!showFilters}
                  aria-label={t('data.entry.toggleFilters')}
                  className="relative"
                >
                  <Filter className="h-4 w-4" />
                  {/* Dot indicator so an active filter is discoverable even while
                      the panel is collapsed. */}
                  {hasActiveFilter && !showFilters && (
                    <span className="absolute -right-0.5 -top-0.5 h-2 w-2 rounded-full bg-primary ring-2 ring-background" />
                  )}
                </Button>
              </TooltipTrigger>
              <TooltipContent>{t('data.entry.toggleFilters')}</TooltipContent>
            </Tooltip>
            {showFilters && (
              <Tooltip>
                <TooltipTrigger asChild>
                  {/* Span wrapper so the tooltip still fires while the button is
                      disabled (no active filter to clear). */}
                  <span className="inline-flex">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={onClearFilters}
                      disabled={!hasActiveFilter}
                      aria-label={t('data.entry.clearFilters')}
                    >
                      <FilterX className="h-4 w-4" />
                    </Button>
                  </span>
                </TooltipTrigger>
                <TooltipContent>{t('data.entry.clearFilters')}</TooltipContent>
              </Tooltip>
            )}
          </>
        )}
        {onShowDeletedChange && (
          <label className="flex items-center gap-1.5 text-xs text-muted-foreground select-none cursor-pointer">
            <input
              type="checkbox"
              checked={!!showDeleted}
              onChange={(e) => onShowDeletedChange(e.target.checked)}
              className="h-3.5 w-3.5 rounded border-field-border text-primary focus:ring-1 focus:ring-primary"
            />
            {t('data.entry.showDeleted')}
          </label>
        )}
        {onExport && (
          <>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onExport('csv')}
              disabled={exporting}
              aria-label={t('data.entry.exportCsvAria')}
            >
              <FileText className="h-4 w-4" />
              {t('data.entry.exportCsv')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onExport('xlsx')}
              disabled={exporting}
              aria-label={t('data.entry.exportXlsxAria')}
            >
              <FileSpreadsheet className="h-4 w-4" />
              {t('data.entry.exportXlsx')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onExport('pdf')}
              disabled={exporting}
              aria-label={t('data.entry.exportPdfAria')}
            >
              <Download className="h-4 w-4" />
              {t('data.entry.exportPdf')}
            </Button>
          </>
        )}
        {canCreate && onCreate && (
          <Button onClick={onCreate}>
            <Plus className="h-4 w-4" />
            {t('data.entry.newRecord')}
          </Button>
        )}
      </div>
    </div>
  )
}
