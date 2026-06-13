export interface MenuIcon {
  type: 'lucide' | 'minio'
  name?: string         // lucide icon name; Story 4.3 validates against lucide-react icon list
  objectKey?: string    // MinIO object key; Story 4.3 uploads via /api/admin/menus/upload-icon
}

// Story 5.2 — async provisioning state on each menu item. null on every field
// when the menu is an unbound section header. FR-54 (Story 3.11) adds
// 'NotApplicable' for VIEW-mode bindings, which are never provisioned.
export type ProvisioningStatus = 'Pending' | 'Success' | 'Error' | 'NotApplicable'

export interface MenuItem {
  id: string
  name: string
  order: number
  icon: MenuIcon | null
  isActive: boolean
  parentId: string | null
  allowedRoleIds: string[]  // populated by Story 4.4; always [] for now
  createdAt: string
  updatedAt: string | null
  // Story 5.2 — Designer binding. All four fields are null for unbound section headers.
  designerId: string | null
  boundVersion: number | null
  provisioningStatus: ProvisioningStatus | null
  provisioningError: string | null
  // Custom route path — alternative to the Designer binding (mutually exclusive).
  // When set, designerId/boundVersion/provisioning* are all null.
  routePath: string | null
}

// Story 5.2 — GET /api/admin/menus/{id}/binding-diff response shape.
export interface BindingInfo {
  designerId: string
  version: number
}
export interface BindingDiffResponse {
  currentBinding: BindingInfo | null
  targetVersion: number
  columnsToAdd: string[]
  columnsAlreadyPresent: string[]
  orphanedColumns: string[]
  willTriggerChildProvisioning: string[]
  estimatedDdl: string[]
}

// Story 4.7 — public navbar node returned by GET /api/menus. Server has already
// filtered by role intersection + isActive, so this shape omits allowedRoleIds
// and isActive. Top-level items contain their sub-menu children inline.
// designerId carries the menu→designer binding (Story 5.2) so the navbar can
// route a click on a bound menu to /data/{designerId}. null for section
// headers and unbound menus.
export interface NavMenuItem {
  id: string
  name: string
  order: number
  icon: MenuIcon | null
  parentId: string | null
  designerId: string | null
  // Custom route path — alternative to designerId (mutually exclusive). When set,
  // the navbar links straight to this path ("/internal" or "https://external")
  // instead of /data/{designerId}. null for bound or section-header menus.
  routePath: string | null
  children: NavMenuItem[]
}

export interface MenuListItem {
  id: string
  name: string
  order: number
  isActive: boolean
  parentId: string | null
  createdAt: string
}

export interface CreateMenuRequest {
  name: string
  order: number
  icon?: MenuIcon | null
  isActive?: boolean   // defaults to true server-side
  parentId?: string | null   // null or omitted = top-level (Story 4.2)
}

export interface UpdateMenuRequest {
  name: string
  order: number
  icon: MenuIcon | null
  isActive: boolean
}
