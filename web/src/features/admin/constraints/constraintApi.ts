import { httpClient } from '../../auth/httpClient'

// Admin UNIQUE-constraint management for provisioned CRUD tables. Mirrors the
// backend DTOs in Features/Designer/Dtos/UniqueConstraintDtos.cs and the
// endpoints under /api/admin/designers.

// One CRUD designer that has a provisioned table (picker row).
export interface ProvisionedDesigner {
  designerId: string
  displayName: string
  boundVersion: number | null
  menuNames: string[]
}

export interface ProvisionedDesignersResponse {
  designers: ProvisionedDesigner[]
}

// A column eligible to take part in a UNIQUE constraint (user fields only).
export interface ConstrainableColumn {
  columnName: string
  pgDataType: string
}

// An existing UNIQUE constraint, columns in declared order.
export interface UniqueConstraint {
  name: string
  columns: string[]
}

export interface UniqueConstraintsResponse {
  designerId: string
  tableName: string
  columns: ConstrainableColumn[]
  constraints: UniqueConstraint[]
}

export function listProvisionedDesigners(): Promise<ProvisionedDesignersResponse> {
  return httpClient.get<ProvisionedDesignersResponse>('/api/admin/designers/provisioned')
}

export function getUniqueConstraints(designerId: string): Promise<UniqueConstraintsResponse> {
  return httpClient.get<UniqueConstraintsResponse>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/unique-constraints`,
  )
}

export function addUniqueConstraint(
  designerId: string,
  columns: string[],
): Promise<UniqueConstraint> {
  return httpClient.post<UniqueConstraint>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/unique-constraints`,
    { columns },
  )
}

export function dropUniqueConstraint(designerId: string, constraintName: string): Promise<void> {
  return httpClient.delete<void>(
    `/api/admin/designers/${encodeURIComponent(designerId)}/unique-constraints/${encodeURIComponent(
      constraintName,
    )}`,
  )
}
