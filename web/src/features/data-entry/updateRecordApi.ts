import { httpClient } from '../auth/httpClient'
import type { RecordPayloadWithChildren } from './createRecordApi'
import type { DynamicRecord } from './recordListApi'

// Story 6.4 — typed client for PUT /api/data/{designerId}/{id}. Partial-update
// semantics: fields absent from `payload` retain their existing DB values.
// Server response shape matches DynamicRecord (AR-46 Option C hybrid: system
// columns camelCase, user fieldKeys verbatim).
//
// Story 6.7 — optional `children` key triggers nested transactional writes
// (UPDATE / INSERT / SOFT-DELETE of child Repeater rows).
// `authFilterColumn` (optional, TreeView per-component auth filter) scopes the edit to
// the requesting user: the row must be owned by them, else 404; the owner column is
// re-stamped so ownership can't be reassigned.
export function updateRecord(
  designerId: string,
  id: string,
  payload: RecordPayloadWithChildren,
  authFilterColumn?: string,
): Promise<DynamicRecord> {
  const qs = authFilterColumn
    ? `?authFilterColumn=${encodeURIComponent(authFilterColumn)}`
    : ''
  return httpClient.put<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}${qs}`,
    payload,
  )
}
