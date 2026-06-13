// Mirrors the backend DTOs at src/FormForge.Api/Features/Roles/Dtos/.
// Keep in sync when the wire shape changes.

export interface PermissionRecord {
  resourceId: string
  canCreate: boolean
  canRead: boolean
  canUpdate: boolean
  canDelete: boolean
  canExport: boolean
}

export interface RoleDetail {
  id: string
  name: string
  description: string | null
  isSystem: boolean
  createdAt: string
  updatedAt: string | null
  permissions: PermissionRecord[]
  canManageDatasets: boolean
}

export interface CreateRoleRequest {
  name: string
  description?: string | null
  permissions: PermissionRecord[]
}

export interface UpdateRoleRequest {
  name: string
  description?: string | null
  permissions: PermissionRecord[]
  canManageDatasets: boolean
}
