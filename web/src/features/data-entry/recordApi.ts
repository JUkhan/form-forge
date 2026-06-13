import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

export type { DynamicRecord }

// Story 6.2 — typed client for GET /api/data/{designerId}/{id}.
//
// When ?include=children is requested, the server adds a top-level "children"
// property keyed by child Repeater designerId. DynamicRecord's index signature
// already accepts that key as `unknown`; this alias narrows it so callers don't
// need to cast at the call site.
export interface RecordWithChildren extends DynamicRecord {
  children?: Record<string, DynamicRecord[]>
}

export function getRecord(
  designerId: string,
  id: string,
  includeChildren = false,
): Promise<RecordWithChildren> {
  const qs = includeChildren ? '?include=children' : ''
  return httpClient.get<RecordWithChildren>(
    `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}${qs}`,
  )
}
