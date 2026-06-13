import { httpClient } from '../auth/httpClient'

// Story 8.9 (FR-61 / AR-65 / AR-69) — dataset audit log API. Matches
// GET /api/admin/datasets/audit → PagedResult<DatasetAuditEntry>.
// Platform-admin only. Optional datasetName and operation filters ANDed server-side.

export interface DatasetAuditEntry {
  id: string
  timestamp: string            // ISO-8601 DateTimeOffset
  actorId: string | null
  actorName: string | null
  datasetName: string
  operation: string            // "CREATE" | "UPDATE" | "DELETE"
  previousValues: string | null  // raw JSONB string
  newValues: string | null       // raw JSONB string
  ddl: string | null
  succeeded: boolean           // false when the transaction was rolled back (FR-61 AC-2)
  correlationId: string | null
}

export interface DatasetAuditPagedResult {
  data: DatasetAuditEntry[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface GetDatasetAuditLogParams {
  page: number
  pageSize: number
  datasetName?: string
  operation?: string
}

export function getDatasetAuditLog(params: GetDatasetAuditLogParams): Promise<DatasetAuditPagedResult> {
  const qs = new URLSearchParams({
    page: String(params.page),
    pageSize: String(params.pageSize),
  })
  if (params.datasetName) qs.set('datasetName', params.datasetName)
  if (params.operation) qs.set('operation', params.operation)
  return httpClient.get<DatasetAuditPagedResult>(`/api/admin/datasets/audit?${qs}`)
}
