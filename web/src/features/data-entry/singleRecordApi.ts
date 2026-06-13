import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'
import type { RecordPayloadWithChildren } from './createRecordApi'

// Typed client for the SingleRecord component endpoints under
// /api/data/{designerId}/single. Unlike the generic record endpoints, the owning
// user is scoped by the `authFilterColumn` the component names — the server resolves
// the current user id from the JWT and enforces it, so the client only passes the
// column name (never a user id).

const base = (designerId: string) => `/api/data/${encodeURIComponent(designerId)}/single`

// Returns the current user's single record, or `undefined` when none exists yet
// (the endpoint replies 204 No Content, which httpClient resolves to undefined).
// Pass includeChildren so a form with Repeaters loads its existing rows.
export function getSingleRecord(
  designerId: string,
  authFilterColumn: string,
  includeChildren = false,
): Promise<DynamicRecord | undefined> {
  const sp = new URLSearchParams({ authFilterColumn })
  if (includeChildren) sp.set('include', 'children')
  return httpClient.get<DynamicRecord | undefined>(`${base(designerId)}?${sp.toString()}`)
}

// Creates the user's record. The server stamps `authFilterColumn` with the current
// user id so a subsequent getSingleRecord finds it.
export function createSingleRecord(
  designerId: string,
  authFilterColumn: string,
  payload: RecordPayloadWithChildren,
): Promise<DynamicRecord> {
  const sp = new URLSearchParams({ authFilterColumn })
  return httpClient.post<DynamicRecord>(`${base(designerId)}?${sp.toString()}`, payload)
}

// Updates the user's existing record. The server verifies the row's
// `authFilterColumn` equals the current user id before writing (404 otherwise).
export function updateSingleRecord(
  designerId: string,
  recordId: string,
  authFilterColumn: string,
  payload: RecordPayloadWithChildren,
): Promise<DynamicRecord> {
  const sp = new URLSearchParams({ authFilterColumn })
  return httpClient.put<DynamicRecord>(
    `${base(designerId)}/${encodeURIComponent(recordId)}?${sp.toString()}`,
    payload,
  )
}
