import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../../datasets/datasetApi'

// Admin "Table Provisioned" tab. Provision a CRUD Designer's physical table
// directly, without binding it to a menu. Mirrors the backend DTOs in
// Features/Provisioning/Dtos/TableProvisioningDtos.cs and the endpoints under
// /api/admin/table-provisioning.

export interface TableProvisioningItem {
  designerId: string
  displayName: string
  tableName: string
  // Whether the physical table currently exists in the database.
  isProvisioned: boolean
  // Published versions (descending) the admin may provision/sync to.
  publishedVersions: number[]
  latestVersion: number | null
  // From the most recent CREATE|ALTER audit row (null = never provisioned).
  lastProvisionedVersion: number | null
  lastOperation: string | null
  lastProvisionedAt: string | null
}

export type TableProvisioningPage = PagedResult<TableProvisioningItem>

export function listTableProvisioning(params: {
  page: number
  pageSize: number
  search?: string
}): Promise<TableProvisioningPage> {
  const query = new URLSearchParams({
    page: String(params.page),
    pageSize: String(params.pageSize),
  })
  if (params.search) query.set('search', params.search)
  return httpClient.get<TableProvisioningPage>(`/api/admin/table-provisioning?${query.toString()}`)
}

// 202 Accepted — the menu-less provisioning job is enqueued and the table is
// created/synced asynchronously. Resolves with void on success.
export function provisionTable(designerId: string, version: number): Promise<void> {
  return httpClient.post<void>(
    `/api/admin/table-provisioning/${encodeURIComponent(designerId)}/provision`,
    { version },
  )
}
