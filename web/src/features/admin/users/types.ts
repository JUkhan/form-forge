// Mirrors the backend DTOs at src/FormForge.Api/Features/Users/Dtos/.
// Keep in sync when the wire shape changes.

export interface UserListItem {
  id: string
  email: string
  displayName: string
  isActive: boolean
  roleCount: number
  createdAt: string
}

export interface UserRoleItem {
  id: string
  name: string
}

export interface UserDetail {
  id: string
  email: string
  displayName: string
  isActive: boolean
  createdAt: string
  updatedAt: string | null
  roles: UserRoleItem[]
  mfaEnabled: boolean // Story 2.15
}

// Story 2.10 — POST /api/admin/users returns the created user plus a non-fatal
// warnings array (e.g. the welcome email could not be sent).
export interface CreateUserResponse extends UserDetail {
  warnings: string[]
}

export interface PagedResult<T> {
  data: T[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface CreateUserRequest {
  email: string
  displayName: string
  temporaryPassword: string
}

export interface UpdateUserRequest {
  displayName?: string
  newPassword?: string
}

export interface AssignUserRolesRequest {
  roleIds: string[]
}
