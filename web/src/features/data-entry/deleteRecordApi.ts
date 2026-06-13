import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

// Story 6.5 — typed client for DELETE /api/data/{designerId}/{id}. Returns the
// updated record (isDeleted: true) per AR-46 Option C on 200. Cascade fan-out
// to Repeater children is server-driven; the client just receives the parent
// row back with cascadeEventId set (non-null when children were touched).
// `authFilterColumn` (optional, TreeView per-component auth filter) scopes the delete to
// the requesting user: the row must be owned by them, else 404.
export function deleteRecord(
  designerId: string,
  id: string,
  authFilterColumn?: string,
): Promise<DynamicRecord> {
  const qs = authFilterColumn
    ? `?authFilterColumn=${encodeURIComponent(authFilterColumn)}`
    : ''
  return httpClient.delete<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}${qs}`,
  )
}
