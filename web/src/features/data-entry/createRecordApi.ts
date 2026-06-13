import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

// Story 6.7 — payload type extended for nested Repeater writes. The optional
// `children` key carries a map of child designerId → array of child records.
// A child record with an `id` is UPDATE semantics (on PUT); without `id` it
// is INSERT. Omitted existing children are soft-deleted (PUT path).
export interface ChildRecordPayload {
  id?: string
  [fieldKey: string]: unknown
}

export interface RecordPayloadWithChildren {
  children?: Record<string, ChildRecordPayload[]>
  [fieldKey: string]: unknown
}

// Story 6.3 — typed client for POST /api/data/{designerId}.
// Server response shape matches DynamicRecord (AR-46 Option C hybrid: system
// columns camelCase, user fieldKeys verbatim). Unknown payload fields are
// silently dropped server-side, so the caller can pass form state as-is.
export function createRecord(
  designerId: string,
  payload: RecordPayloadWithChildren,
): Promise<DynamicRecord> {
  return httpClient.post<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}`,
    payload,
  )
}
