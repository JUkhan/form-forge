import { httpClient } from '../../auth/httpClient'

// Story 5.7 — admin audit log API. Matches GET /api/admin/designers/{designerId}/audit
// → PagedResult<SchemaAuditEntry>. designerId is URL-encoded (defence-in-depth;
// SafeIdentifier already restricts it server-side).

export interface SchemaAuditEntry {
  id: string
  timestamp: string          // ISO-8601 DateTimeOffset
  actorId: string | null
  actorName: string | null
  designerId: string
  fromVersion: number | null
  toVersion: number          // 0 = sentinel for DROP (not a real version)
  ddlOperation: string       // "CREATE" | "ALTER" | "DROP"
  columnsAdded: string[] | null
  columnsDropped: string[] | null
  columnsDiff: string | null
  correlationId: string | null
  notes: string | null
}

export interface AuditPagedResult {
  data: SchemaAuditEntry[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export function getSchemaAuditLog(
  designerId: string,
  page: number,
  pageSize: number,
): Promise<AuditPagedResult> {
  return httpClient.get<AuditPagedResult>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/audit?page=${page}&pageSize=${pageSize}`,
  )
}
