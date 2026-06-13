import { httpClient } from '../auth/httpClient'
import { refreshSession } from '../auth/refreshCoordinator'
import { tokenStore } from '../auth/tokenStore'
import { generateCorrelationId } from '../../lib/correlationId'
import { ApiError } from '../../lib/api/apiError'

// Story 8.10 (FR-62) — TypeScript API layer for the Dataset Manager UI. All 8
// backend endpoints already exist (Stories 8.1–8.9); this module only mirrors
// their request/response shapes and delegates transport to httpClient.

export interface PagedResult<T> {
  data: T[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

// Mirrors DatasetSummaryDto.cs — list projection (no query/builderState/version)
export interface DatasetSummary {
  id: string
  datasetName: string
  isCustomQuery: boolean
  createdAt: string // ISO-8601
  updatedAt: string | null
  createdByName: string | null
}

// Mirrors DatasetDto.cs — full shape returned by GET /{id}, POST, PUT
export interface DatasetDetail {
  id: string
  datasetName: string
  isCustomQuery: boolean
  query: string | null
  builderState: string | null
  version: number // optimistic concurrency token (Decision 6.4)
  createdAt: string
  createdBy: string | null
}

export interface CreateDatasetPayload {
  datasetName: string
  isCustomQuery: boolean
  query: string | null
  // Story 11.2 (FR-71 AC-4) — optional builder_state blob for builder-mode creates;
  // the server re-derives SQL from it (CreateAsync checkpoint b). The current UI never
  // sends one (datasets are created in placeholder mode, then saved from the canvas).
  builderState?: string | null
}

// version is REQUIRED — omitting it causes a 400 (UpdateDatasetRequest.cs has non-nullable int Version)
export interface UpdateDatasetPayload {
  datasetName?: string
  isCustomQuery?: boolean
  query?: string | null
  builderState?: string | null
  version: number
}

export interface ListDatasetsParams {
  // Case-insensitive contains match on dataset_name.
  search?: string
  // Whitelisted single-column sort, "field:dir" (e.g. "name:asc"). Server falls back to
  // newest-first on an unknown/empty value.
  sort?: string
}

export function listDatasets(
  page: number,
  pageSize: number,
  params?: ListDatasetsParams,
): Promise<PagedResult<DatasetSummary>> {
  const sp = new URLSearchParams()
  sp.set('page', String(page))
  sp.set('pageSize', String(pageSize))
  if (params?.search) sp.set('search', params.search)
  if (params?.sort) sp.set('sort', params.sort)
  return httpClient.get<PagedResult<DatasetSummary>>(`/api/datasets?${sp.toString()}`)
}

export function getDataset(id: string): Promise<DatasetDetail> {
  return httpClient.get<DatasetDetail>(`/api/datasets/${id}`)
}

export function createDataset(payload: CreateDatasetPayload): Promise<DatasetDetail> {
  return httpClient.post<DatasetDetail>('/api/datasets', payload)
}

export function updateDataset(id: string, payload: UpdateDatasetPayload): Promise<DatasetDetail> {
  return httpClient.put<DatasetDetail>(`/api/datasets/${id}`, payload)
}

export function deleteDataset(id: string): Promise<void> {
  return httpClient.delete<void>(`/api/datasets/${id}`)
}

// Story 11.3 (FR-72 / AR-63) — read-only LIMIT-10 preview. isCustomQuery selects which
// payload the server runs: query (raw SELECT, custom mode) or builderState (re-derived to
// SQL, builder mode). Mirrors PreviewRequest.cs / PreviewResultDto.cs.
export interface PreviewDatasetPayload {
  isCustomQuery: boolean
  query?: string | null
  builderState?: string | null
}

export interface PreviewResult {
  columns: string[]
  // Cells are whatever System.Text.Json emits for the column's CLR type; DateTime
  // values arrive as ISO-8601 strings.
  rows: (string | number | boolean | null)[][]
}

export function previewDataset(payload: PreviewDatasetPayload): Promise<PreviewResult> {
  return httpClient.post<PreviewResult>('/api/datasets/preview', payload)
}

// Story 9.1 (FR-63 / AR-62) — catalog response mirrors CatalogDto.cs
export interface CatalogColumn {
  columnName: string
  pgType: string
  isNullable: boolean
}

// The catalog LIST carries names + column counts only — columns are fetched lazily
// per table (getTableColumns) so the palette scales to thousands of tables.
export interface CatalogTable {
  tableName: string
  columnCount: number
}

export interface CatalogResponse {
  tables: CatalogTable[]
}

// One table's columns, fetched on demand (e.g. when dragged onto the canvas).
export interface TableColumns {
  tableName: string
  columns: CatalogColumn[]
}

export function getCatalog(): Promise<CatalogResponse> {
  return httpClient.get<CatalogResponse>('/api/datasets/catalog')
}

export function getTableColumns(tableName: string): Promise<TableColumns> {
  return httpClient.get<TableColumns>(
    `/api/datasets/catalog/${encodeURIComponent(tableName)}`,
  )
}

// Designer Dropdown "Dataset" source — the backing VIEW's column names, for the
// inspector's Label/Value field comboboxes. Mirrors DatasetColumnsDto.cs.
export interface DatasetColumns {
  id: string
  datasetName: string
  columns: string[]
}

export function getDatasetColumns(id: string): Promise<DatasetColumns> {
  return httpClient.get<DatasetColumns>(`/api/datasets/${id}/columns`)
}

// Name-keyed columns lookup. A Dataset-backed Dropdown stores the dataset's VIEW
// NAME (optionsDatasetId) rather than its GUID — mirroring how a Designer-backed
// Dropdown stores a table name — so its inspector resolves columns by name.
export function getDatasetColumnsByName(name: string): Promise<DatasetColumns> {
  return httpClient.get<DatasetColumns>(
    `/api/datasets/by-name/${encodeURIComponent(name)}/columns`,
  )
}

// One {value,label} option for a Dataset-backed dropdown. Mirrors the runtime
// Designer-backed dropdown's option shape (value always a string; label nullable).
export interface DatasetOption {
  value: string
  label: string | null
}

export interface DatasetOptionsParams {
  labelField: string
  valueField: string
  search?: string
  // Exact value match — resolves the label for a saved selection that isn't on the
  // first page of results. Filters on the value column, not the searchable label.
  value?: string
  // Cascading "Depends on" filters: target column → value (already resolved from the
  // dependsOn local:target map against the form's data). Mirrors the Designer source.
  filter?: Record<string, string>
  page?: number
  pageSize?: number
}

// GET /api/datasets/by-name/{name}/options — paginated {value,label} options from a
// dataset's backing VIEW, limited to the chosen label + value columns. `name` is the
// dataset's VIEW name (a Dataset-backed Dropdown stores it in optionsDatasetId, not a
// GUID — so both the runtime fetch here and the record-list label column key off it).
export function fetchDatasetOptions(
  name: string,
  params: DatasetOptionsParams,
): Promise<PagedResult<DatasetOption>> {
  const sp = new URLSearchParams()
  sp.set('labelField', params.labelField)
  sp.set('valueField', params.valueField)
  if (params.search) sp.set('search', params.search)
  if (params.value) sp.set('value', params.value)
  sp.set('page', String(params.page ?? 1))
  sp.set('pageSize', String(params.pageSize ?? 50))
  if (params.filter) {
    for (const [k, v] of Object.entries(params.filter)) sp.append(`filter[${k}]`, v)
  }
  return httpClient.get<PagedResult<DatasetOption>>(
    `/api/datasets/by-name/${encodeURIComponent(name)}/options?${sp.toString()}`,
  )
}

// DatasetComponent runtime data-view — paginated rows from a dataset's VIEW.
export interface DatasetSortSpec {
  column: string
  direction: 'asc' | 'desc'
}

export interface DatasetRowsRequest {
  // ResolvedFilterGroup (or null/omitted for no filter); kept as unknown so the
  // designer's datasetFilter resolver stays the single source of the shape.
  filters?: unknown
  sort?: DatasetSortSpec[]
  columns?: string[]
  page?: number
  pageSize?: number
  // The DatasetComponent's authFilterColumn property. The server scopes rows to
  // those whose value equals the requesting user's id (value resolved server-side
  // from the JWT — only the column name travels from the client).
  authFilterColumn?: string
}

export interface DatasetRowsPage {
  data: Record<string, unknown>[]
  total: number
  page: number
  pageSize: number
  totalPages: number
  columns: string[]
}

export function fetchDatasetRows(id: string, body: DatasetRowsRequest): Promise<DatasetRowsPage> {
  return httpClient.post<DatasetRowsPage>(`/api/datasets/${id}/rows`, body)
}

// Chart aggregation (Phase 2): GROUP BY category, aggregate(value).
export type DatasetAggregate = 'count' | 'sum' | 'avg' | 'min' | 'max'

export interface DatasetChartRequest {
  filters?: unknown
  categoryColumn: string
  valueColumn?: string // ignored for 'count'
  aggregate: DatasetAggregate
  authFilterColumn?: string
}

export interface DatasetChartPoint {
  category: string
  value: number
}

export interface DatasetChartData {
  categoryColumn: string
  valueColumn: string | null
  aggregate: string
  points: DatasetChartPoint[]
}

export function fetchDatasetChart(id: string, body: DatasetChartRequest): Promise<DatasetChartData> {
  return httpClient.post<DatasetChartData>(`/api/datasets/${id}/chart`, body)
}

export interface DatasetExportColumn {
  column: string
  header?: string
}

export interface DatasetExportRequest {
  filters?: unknown
  sort?: DatasetSortSpec[]
  columns?: DatasetExportColumn[]
  authFilterColumn?: string
}

export type DatasetExportFormat = 'csv' | 'xlsx' | 'pdf'

// POST the filter/sort/column body and stream the rendered file. Mirrors the record-export
// blob+<a download> dance (the endpoint requires a Bearer token, so window.open won't do).
export async function downloadDatasetExport(
  id: string,
  format: DatasetExportFormat,
  body: DatasetExportRequest,
): Promise<void> {
  const url = `/api/datasets/${id}/rows/export?format=${format}`
  const send = () =>
    fetch(url, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${tokenStore.get() ?? ''}`,
        'Content-Type': 'application/json',
        'X-Correlation-ID': generateCorrelationId(),
      },
      body: JSON.stringify(body),
    })

  let response = await send()
  if (response.status === 401) {
    const refreshed = await refreshSession()
    if (refreshed) {
      tokenStore.set(refreshed.accessToken)
      response = await send()
    } else {
      tokenStore.clear()
      window.location.replace('/login')
    }
  }
  if (!response.ok) {
    let messageKey = 'errors.exportFailed'
    let code = 'EXPORT_FAILED'
    let detail = `Export failed with status ${response.status}.`
    try {
      const b = (await response.json()) as { code?: string; messageKey?: string; detail?: string }
      if (b.code) code = b.code
      if (b.messageKey) messageKey = b.messageKey
      if (b.detail) detail = b.detail
    } catch {
      /* not JSON — keep defaults */
    }
    throw new ApiError(response.status, code, messageKey, detail)
  }

  const disposition = response.headers.get('Content-Disposition') ?? ''
  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(disposition)
  const plain = /filename=("([^"]+)"|([^;]+))/i.exec(disposition)
  const filename = star
    ? decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''))
    : plain
      ? (plain[2] ?? plain[3] ?? `dataset.${format}`).trim()
      : `dataset.${format}`

  const blob = await response.blob()
  const objectUrl = URL.createObjectURL(blob)
  try {
    const a = document.createElement('a')
    a.href = objectUrl
    a.download = filename
    document.body.appendChild(a)
    a.click()
    a.remove()
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}
