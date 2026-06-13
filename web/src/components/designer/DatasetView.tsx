import { useMemo, useState, type ComponentProps } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { toast } from 'sonner'
import {
  ArrowDown,
  ArrowUp,
  BarChart3 as BarChartIcon,
  ChartLine as LineChartIcon,
  ChartPie as PieChartIcon,
  CircleDashed as DonutIcon,
  Download,
  FileSpreadsheet,
  FileText,
  Filter,
  FilterX,
  Table2,
} from 'lucide-react'
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip as RechartsTooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { DesignerElement } from '@/types/designer'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import {
  downloadDatasetExport,
  fetchDatasetChart,
  fetchDatasetRows,
  type DatasetAggregate,
  type DatasetExportFormat,
  type DatasetSortSpec,
} from '@/features/datasets/datasetApi'
import ElementRenderer, { type InteractiveFormProps } from './ElementRenderer'
import { parseDatasetFilter, resolveDatasetFilter } from './datasetFilter'
import { parseTableColumns, resolveVisibleColumns } from './datasetTable'

const PAGE_SIZE = 25
const EMPTY_ERRORS: Map<string, string> = new Map()
const EMPTY_HIDDEN: Set<string> = new Set()

// Themeable palette (oklch theme tokens) for pie/donut slices.
const CHART_PALETTE = [
  'var(--chart-1)',
  'var(--chart-2)',
  'var(--chart-3)',
  'var(--chart-4)',
  'var(--chart-5)',
]
const TOOLTIP_STYLE = {
  background: 'var(--card)',
  border: '1px solid var(--border)',
  borderRadius: 6,
  fontSize: 12,
  color: 'var(--foreground)',
}
const AXIS_TICK = { fontSize: 11, fill: 'var(--muted-foreground)' }

type ViewKey = 'table' | 'bar' | 'line' | 'pie' | 'donut'

function formatCell(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'boolean') return value ? 'true' : 'false'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

// An icon Button with a shadcn tooltip (replaces the native title attribute).
function IconBtn({ tip, children, ...props }: { tip: string } & ComponentProps<typeof Button>) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button {...props}>{children}</Button>
      </TooltipTrigger>
      <TooltipContent>{tip}</TooltipContent>
    </Tooltip>
  )
}

// Runtime renderer for a DatasetComponent: a toolbar (view-type switcher + filter toggle/clear +
// exports) over either a paginated/sortable table or a bar/line/pie/donut chart of the dataset's
// rows. Self-contained — it manages its own filter-form state and never touches the parent formData.
export default function DatasetView({ element }: { element: DesignerElement }) {
  const { t } = useTranslation()
  const p = element.properties
  const datasetId = typeof p.optionsDatasetId === 'string' ? p.optionsDatasetId.trim() : ''
  const filterable = p.filterable === true
  const tableView = p.tableView === true
  // Auth filter column — when set, every view (rows / chart / export) is scoped
  // server-side to rows whose value equals the requesting user's id.
  const authFilterColumn = typeof p.authFilterColumn === 'string' ? p.authFilterColumn.trim() : ''

  const filterGroup = useMemo(() => parseDatasetFilter(p.filterConditions), [p.filterConditions])
  const tableColumns = useMemo(() => parseTableColumns(p.tableColumns), [p.tableColumns])

  // Chart config (shared across all enabled chart types).
  const chartCategory = typeof p.chartCategory === 'string' ? p.chartCategory : ''
  const chartValue = typeof p.chartValue === 'string' ? p.chartValue : ''
  const chartAggregate = (typeof p.chartAggregate === 'string' ? p.chartAggregate : 'count') as DatasetAggregate

  // Which views the author enabled, in toolbar order.
  const views = useMemo<{ key: ViewKey; label: string; Icon: typeof Table2 }[]>(() => {
    const out: { key: ViewKey; label: string; Icon: typeof Table2 }[] = []
    if (tableView) out.push({ key: 'table', label: t('dataset.tableView'), Icon: Table2 })
    if (p.barChart === true) out.push({ key: 'bar', label: t('dataset.barChart'), Icon: BarChartIcon })
    if (p.lineChart === true) out.push({ key: 'line', label: t('dataset.lineChart'), Icon: LineChartIcon })
    if (p.pieChart === true) out.push({ key: 'pie', label: t('dataset.pieChart'), Icon: PieChartIcon })
    if (p.donutChart === true) out.push({ key: 'donut', label: t('dataset.donutChart'), Icon: DonutIcon })
    return out
  }, [tableView, p.barChart, p.lineChart, p.pieChart, p.donutChart, t])

  const [requestedView, setRequestedView] = useState<ViewKey | null>(null)
  // Resolve to a still-enabled view (the requested one, else the first available).
  const activeView: ViewKey | null =
    (requestedView && views.some((v) => v.key === requestedView) ? requestedView : views[0]?.key) ?? null

  // Filter-form state (local — the filter inputs are this view's, not the outer form's).
  const [filterValues, setFilterValues] = useState<Record<string, unknown>>({})
  const [filterOpen, setFilterOpen] = useState(false)
  // The filter actually in effect (set on Apply / Clear). Kept separate from the live
  // form values so typing in the form doesn't refetch on every keystroke.
  const [appliedFilter, setAppliedFilter] = useState<unknown>(null)
  const [sort, setSort] = useState<DatasetSortSpec[]>([])
  const [page, setPage] = useState(1)
  const [exporting, setExporting] = useState(false)

  const filterInteractive = useMemo<InteractiveFormProps>(
    () => ({
      formData: filterValues,
      onChange: (key, value) => setFilterValues((prev) => ({ ...prev, [key]: value })),
      onPrimaryButtonClick: () => {},
      errorByBindTo: EMPTY_ERRORS,
      formIsValid: true,
      hiddenElementIds: EMPTY_HIDDEN,
    }),
    [filterValues],
  )

  const rowsQuery = useQuery({
    queryKey: ['dataset-rows', datasetId, appliedFilter, sort, page, authFilterColumn],
    queryFn: () =>
      fetchDatasetRows(datasetId, {
        filters: appliedFilter ?? undefined,
        sort,
        page,
        pageSize: PAGE_SIZE,
        authFilterColumn: authFilterColumn || undefined,
      }),
    enabled: datasetId !== '' && activeView === 'table',
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })

  const dataColumns = rowsQuery.data?.columns
  const visibleColumns = useMemo(
    () => resolveVisibleColumns(tableColumns, dataColumns ?? []),
    [tableColumns, dataColumns],
  )

  function applyFilter() {
    setAppliedFilter(resolveDatasetFilter(filterGroup, filterValues))
    setPage(1)
  }
  function clearFilter() {
    setFilterValues({})
    setAppliedFilter(null)
    setPage(1)
  }

  function toggleSort(column: string) {
    setSort((prev) => {
      const current = prev[0]
      if (!current || current.column !== column) return [{ column, direction: 'asc' }]
      if (current.direction === 'asc') return [{ column, direction: 'desc' }]
      return [] // third click clears
    })
    setPage(1)
  }

  async function handleExport(format: DatasetExportFormat) {
    if (datasetId === '') return
    setExporting(true)
    try {
      await downloadDatasetExport(datasetId, format, {
        filters: appliedFilter ?? undefined,
        sort,
        columns: visibleColumns.map((c) => ({ column: c.column, header: c.header })),
        authFilterColumn: authFilterColumn || undefined,
      })
    } catch {
      toast.error(t('dataset.exportFailed'))
    } finally {
      setExporting(false)
    }
  }

  if (datasetId === '') {
    return (
      <div className="flex h-20 w-full items-center justify-center rounded-lg border border-dashed border-border bg-muted text-sm text-muted-foreground">
        {t('dataset.notConfigured')}
      </div>
    )
  }

  const total = rowsQuery.data?.total ?? 0
  const totalPages = rowsQuery.data?.totalPages ?? 0
  const rows = rowsQuery.data?.data ?? []
  const sortCol = sort[0]

  return (
    <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-2">
      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1">
          {views.map((v) => (
            <IconBtn
              key={v.key}
              tip={v.label}
              type="button"
              variant={activeView === v.key ? 'default' : 'outline'}
              size="sm"
              className="h-7 gap-1 px-2 text-xs"
              aria-pressed={activeView === v.key}
              onClick={() => setRequestedView(v.key)}
            >
              <v.Icon className="h-4 w-4" />
            </IconBtn>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-1">
          {filterable && (
            <>
              <Button
                type="button"
                variant={filterOpen ? 'default' : 'outline'}
                size="sm"
                className="h-7 gap-1 text-xs"
                aria-pressed={filterOpen}
                onClick={() => setFilterOpen((o) => !o)}
              >
                <Filter className="h-4 w-4" />
                {t('dataset.filter')}
              </Button>
              {/* Only offer Clear when a filter is actually in effect. */}
              {appliedFilter !== null && (
                <IconBtn
                  tip={t('dataset.clearFilter')}
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-7 gap-1 text-xs"
                  onClick={clearFilter}
                >
                  <FilterX className="h-4 w-4" />
                </IconBtn>
              )}
            </>
          )}
          <IconBtn tip="CSV" type="button" variant="outline" size="sm" className="h-7 px-2"
            disabled={exporting} onClick={() => handleExport('csv')}>
            <FileText className="h-4 w-4" />
          </IconBtn>
          <IconBtn tip="XLSX" type="button" variant="outline" size="sm" className="h-7 px-2"
            disabled={exporting} onClick={() => handleExport('xlsx')}>
            <FileSpreadsheet className="h-4 w-4" />
          </IconBtn>
          <IconBtn tip="PDF" type="button" variant="outline" size="sm" className="h-7 px-2"
            disabled={exporting} onClick={() => handleExport('pdf')}>
            <Download className="h-4 w-4" />
          </IconBtn>
        </div>
      </div>

      {/* Filter form */}
      {filterable && filterOpen && (
        <div className="flex flex-col gap-3 rounded-md border border-border bg-muted/40 p-3">
          {(element.children ?? []).map((child) => (
            <ElementRenderer
              key={child.id}
              element={child}
              interactive={true}
              interactiveProps={filterInteractive}
            />
          ))}
          <div className="flex gap-2">
            <Button type="button" size="sm" className="h-7 text-xs" onClick={applyFilter}>
              {t('dataset.applyFilter')}
            </Button>
            <Button type="button" variant="outline" size="sm" className="h-7 text-xs" onClick={clearFilter}>
              {t('dataset.clearFilter')}
            </Button>
          </div>
        </div>
      )}

      {/* Body */}
      {activeView === null ? (
        <p className="px-2 py-4 text-center text-xs text-muted-foreground">
          {t('dataset.noViewEnabled')}
        </p>
      ) : activeView === 'table' ? (
        rowsQuery.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : rowsQuery.isError ? (
          <p className="px-2 py-4 text-center text-xs text-destructive">{t('dataset.loadError')}</p>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-xs">
                <thead>
                  <tr className="border-b border-border">
                    {visibleColumns.map((c) => {
                      const active = sortCol?.column === c.column
                      return (
                        <th key={c.column} className="px-2 py-1.5 text-left font-medium text-muted-foreground">
                          <button
                            type="button"
                            onClick={() => toggleSort(c.column)}
                            className="flex items-center gap-1 hover:text-foreground"
                          >
                            {c.header}
                            {active && (sortCol.direction === 'asc'
                              ? <ArrowUp className="h-3 w-3" />
                              : <ArrowDown className="h-3 w-3" />)}
                          </button>
                        </th>
                      )
                    })}
                  </tr>
                </thead>
                <tbody>
                  {rows.length === 0 ? (
                    <tr>
                      <td colSpan={Math.max(1, visibleColumns.length)} className="px-2 py-6 text-center text-muted-foreground">
                        {t('dataset.noRows')}
                      </td>
                    </tr>
                  ) : (
                    rows.map((row, i) => (
                      <tr key={i} className="border-b border-border/60">
                        {visibleColumns.map((c) => (
                          <td key={c.column} className="px-2 py-1.5 text-foreground">
                            {formatCell(row[c.column])}
                          </td>
                        ))}
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            <div className="flex items-center justify-between px-1 text-xs text-muted-foreground">
              <span>{t('dataset.totalRows', { count: total })}</span>
              <div className="flex items-center gap-2">
                <Button type="button" variant="outline" size="sm" className="h-7 text-xs"
                  disabled={page <= 1} onClick={() => setPage((pg) => Math.max(1, pg - 1))}>
                  {t('dataset.prev')}
                </Button>
                <span>{t('dataset.pageOf', { page, totalPages: Math.max(1, totalPages) })}</span>
                <Button type="button" variant="outline" size="sm" className="h-7 text-xs"
                  disabled={page >= totalPages} onClick={() => setPage((pg) => pg + 1)}>
                  {t('dataset.next')}
                </Button>
              </div>
            </div>
          </>
        )
      ) : (
        <ChartBody
          datasetId={datasetId}
          chartType={activeView}
          category={chartCategory}
          value={chartValue}
          aggregate={chartAggregate}
          filters={appliedFilter}
          authFilterColumn={authFilterColumn}
        />
      )}
    </div>
  )
}

// Fetches aggregated chart data and renders it as the requested chart type. Mounted only
// while a chart view is active, so its query runs on demand; the cache key omits chartType
// so switching bar↔line↔pie reuses the same aggregation.
function ChartBody({
  datasetId,
  chartType,
  category,
  value,
  aggregate,
  filters,
  authFilterColumn,
}: {
  datasetId: string
  chartType: Exclude<ViewKey, 'table'>
  category: string
  value: string
  aggregate: DatasetAggregate
  filters: unknown
  authFilterColumn: string
}) {
  const { t } = useTranslation()
  const needsValue = aggregate !== 'count'

  const query = useQuery({
    queryKey: ['dataset-chart', datasetId, category, value, aggregate, filters, authFilterColumn],
    queryFn: () =>
      fetchDatasetChart(datasetId, {
        filters: filters ?? undefined,
        categoryColumn: category,
        valueColumn: needsValue ? value : undefined,
        aggregate,
        authFilterColumn: authFilterColumn || undefined,
      }),
    enabled: datasetId !== '' && category !== '' && (!needsValue || value !== ''),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })

  if (category === '' || (needsValue && value === '')) {
    return <p className="px-2 py-6 text-center text-xs text-muted-foreground">{t('dataset.chartNotConfigured')}</p>
  }
  if (query.isLoading) return <Skeleton className="h-[300px] w-full" />
  if (query.isError) return <p className="px-2 py-6 text-center text-xs text-destructive">{t('dataset.loadError')}</p>

  const points = query.data?.points ?? []
  if (points.length === 0) {
    return <p className="px-2 py-6 text-center text-xs text-muted-foreground">{t('dataset.noRows')}</p>
  }

  return (
    <div className={cn('h-[300px] w-full')}>
      <ResponsiveContainer width="100%" height="100%">
        {chartType === 'bar' ? (
          <BarChart data={points} margin={{ top: 8, right: 8, bottom: 8, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
            <XAxis dataKey="category" tick={AXIS_TICK} stroke="var(--border)" />
            <YAxis tick={AXIS_TICK} stroke="var(--border)" />
            <RechartsTooltip contentStyle={TOOLTIP_STYLE} cursor={{ fill: 'var(--overlay-hover)' }} />
            <Bar dataKey="value" fill="var(--chart-1)" radius={[3, 3, 0, 0]} />
          </BarChart>
        ) : chartType === 'line' ? (
          <LineChart data={points} margin={{ top: 8, right: 8, bottom: 8, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
            <XAxis dataKey="category" tick={AXIS_TICK} stroke="var(--border)" />
            <YAxis tick={AXIS_TICK} stroke="var(--border)" />
            <RechartsTooltip contentStyle={TOOLTIP_STYLE} />
            <Line type="monotone" dataKey="value" stroke="var(--chart-1)" strokeWidth={2} dot={false} />
          </LineChart>
        ) : (
          <PieChart>
            <Pie
              data={points}
              dataKey="value"
              nameKey="category"
              outerRadius={100}
              innerRadius={chartType === 'donut' ? 55 : 0}
            >
              {points.map((_, i) => (
                <Cell key={i} fill={CHART_PALETTE[i % CHART_PALETTE.length]} />
              ))}
            </Pie>
            <RechartsTooltip contentStyle={TOOLTIP_STYLE} />
            <Legend wrapperStyle={{ fontSize: 11 }} />
          </PieChart>
        )}
      </ResponsiveContainer>
    </div>
  )
}
