import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

// Typed client for DELETE /api/data/{designerId}/{id}/hard-delete. Permanently
// removes a (previously soft-deleted) record AND its whole descendant subtree
// (Repeater children, TreeView nodes) from the database — the server walks the
// cascade in application code so it works even without DB FK constraints. Admin
// only; the server returns 403 FORBIDDEN for non-admins. On 200 it returns the
// last-known record snapshot; the caller uses it only to confirm + drop the row.
export function hardDeleteRecord(
  designerId: string,
  id: string,
): Promise<DynamicRecord> {
  return httpClient.delete<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}/hard-delete`,
  )
}
