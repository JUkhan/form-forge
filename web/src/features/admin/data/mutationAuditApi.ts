import { httpClient } from '../../auth/httpClient'

// Story 6.8 — mutation audit log API. Matches GET /api/admin/data/{designerId}/audit
// → PagedResult<MutationAuditEntry>. designerId is URL-encoded (defence-in-depth;
// SafeIdentifier already restricts it server-side).
export interface MutationAuditEntry {
  id: string
  timestamp: string          // ISO-8601 DateTimeOffset
  actorId: string | null
  actorName: string | null
  designerId: string
  recordId: string
  operation: string          // "CREATE" | "UPDATE" | "SOFT_DELETE" | "RESTORE"
  previousValues: string | null  // raw JSONB string
  newValues: string | null       // raw JSONB string
  correlationId: string | null
}

export interface MutationAuditPagedResult {
  data: MutationAuditEntry[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export function getMutationAuditLog(
  designerId: string,
  page: number,
  pageSize: number,
): Promise<MutationAuditPagedResult> {
  return httpClient.get<MutationAuditPagedResult>(
    `/api/admin/data/${encodeURIComponent(designerId)}/audit?page=${page}&pageSize=${pageSize}`,
  )
}
