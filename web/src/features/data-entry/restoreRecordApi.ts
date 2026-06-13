import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

// Story 6.6 — typed client for PUT /api/data/{designerId}/{id}/restore. Returns
// the restored record (isDeleted: false) per AR-46 Option C on 200. Cascade
// fan-out to descendant rows sharing the same cascade_event_id is server-driven;
// the client just receives the parent row back with cascadeEventId cleared to null.
export function restoreRecord(
  designerId: string,
  id: string,
): Promise<DynamicRecord> {
  return httpClient.put<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}/restore`,
    {},
  )
}
