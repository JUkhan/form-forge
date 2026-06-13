import { httpClient } from '../../auth/httpClient'

// Story 5.6 — admin drift API. Matches /api/admin/designers/{designerId}/drift
// + DELETE /api/admin/designers/{designerId}/columns/{columnName}. The designerId
// is URL-encoded so a leading whitespace / unusual identifier cannot mangle the
// path (SafeIdentifier already restricts it server-side; the encoding is
// defence-in-depth at the wire boundary).
export interface OrphanedColumnInfo {
  columnName: string
  pgDataType: string
  estimatedNonNullRowCount: number
}

export interface SchemaDriftResponse {
  orphanedColumns: OrphanedColumnInfo[]
}

export function getSchemaDrift(designerId: string): Promise<SchemaDriftResponse> {
  return httpClient.get<SchemaDriftResponse>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/drift`,
  )
}

export function dropColumn(designerId: string, columnName: string): Promise<void> {
  return httpClient.delete<void>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/columns/${encodeURIComponent(columnName)}`,
  )
}
