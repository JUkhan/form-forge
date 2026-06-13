import { httpClient } from '../auth/httpClient'

// Story 6.1 — typed client for GET /api/data/{designerId}.
//
// AR-46 (Option C hybrid): the server serializes system column PG names as
// camelCase JSON keys (id, createdAt, isDeleted, etc.) and user fieldKey
// columns verbatim (e.g. `report_title` stays `report_title`). The index
// signature covers user fieldKeys; declared system columns are non-optional.

export interface DynamicRecord {
  id: string
  createdAt: string             // ISO-8601 timestamptz
  createdBy: string | null
  updatedAt: string
  updatedBy: string | null
  isDeleted: boolean
  cascadeEventId: string | null
  [fieldKey: string]: unknown   // user fieldKey columns — verbatim snake_case names
}

export interface RecordPagedResult {
  data: DynamicRecord[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface RecordListParams {
  page?: number
  pageSize?: number
  /** Comma-separated `col:dir` pairs, e.g. `"created_at:desc,title:asc"`. Up to 3. */
  sort?: string
  /** Map of PG-name (snake_case or system camelCase JSON name) → string value. */
  filter?: Record<string, string>
  /**
   * When true, soft-deleted rows are returned alongside live rows. The backend's
   * default is to hide them (Story 7-followup). The UI's "Show deleted" toggle
   * is the only place that flips this to true.
   */
  includeDeleted?: boolean
}

// Builds the query string. `filter[key]=value` is the conventional
// ASP.NET / Rails / OData notation; the server parses it manually because
// Minimal API model binding does not handle the bracket syntax.
function buildQueryString(params: RecordListParams): string {
  const search = new URLSearchParams()
  if (params.page !== undefined) search.set('page', String(params.page))
  if (params.pageSize !== undefined) search.set('pageSize', String(params.pageSize))
  if (params.sort) search.set('sort', params.sort)
  if (params.includeDeleted) search.set('includeDeleted', 'true')
  if (params.filter) {
    for (const [key, value] of Object.entries(params.filter)) {
      search.append(`filter[${key}]`, value)
    }
  }
  const qs = search.toString()
  return qs ? `?${qs}` : ''
}

export function listRecords(
  designerId: string,
  params: RecordListParams,
): Promise<RecordPagedResult> {
  return httpClient.get<RecordPagedResult>(
    `/api/data/${encodeURIComponent(designerId)}${buildQueryString(params)}`,
  )
}
