import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useTranslation } from 'react-i18next'
import { httpClient } from '../../auth/httpClient'
import { ApiError } from '../../../lib/api/apiError'
import type { CreateMenuRequest, MenuItem, MenuListItem, UpdateMenuRequest } from '../../menu/types'
import type { PagedResult } from '../users/types'
import { MENUS_ADMIN_QUERY_KEY } from './useMenusAdminQuery'
import { MENU_DETAIL_QUERY_KEY } from './useMenuDetailQuery'

function invalidateAllMenus(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: MENUS_ADMIN_QUERY_KEY })
}

export function useCreateMenuMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: CreateMenuRequest) =>
      httpClient.post<MenuItem>('/api/admin/menus', body),
    onSuccess: () => {
      invalidateAllMenus(queryClient)
      toast.success(t('admin.menus.createSuccess'))
    },
  })
}

export function useUpdateMenuMutation(menuId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: UpdateMenuRequest) =>
      httpClient.put<void>(`/api/admin/menus/${menuId}`, body),
    onSuccess: () => {
      invalidateAllMenus(queryClient)
      void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
      toast.success(t('admin.menus.updateSuccess'))
    },
  })
}

export function useDeleteMenuMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (menuId: string) =>
      httpClient.delete<void>(`/api/admin/menus/${menuId}`),
    onSuccess: () => {
      invalidateAllMenus(queryClient)
      toast.success(t('admin.menus.deleteSuccess'))
    },
  })
}

// Story 4.3 — multipart upload returns the MinIO object key for use as MenuIcon.
export function useUploadMenuIconMutation() {
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData()
      form.append('file', file)
      return httpClient.post<{ type: 'minio'; objectKey: string }>(
        '/api/admin/menus/upload-icon',
        form,
      )
    },
  })
}

// Story 4.4 — full-replace the allowed-roles set for a menu item.
export function useAssignMenuRolesMutation(menuId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: { roleIds: string[] }) =>
      httpClient.put<void>(`/api/admin/menus/${menuId}/roles`, body),
    onSuccess: () => {
      invalidateAllMenus(queryClient)
      void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
      toast.success(t('admin.menus.rolesAssignSuccess'))
    },
  })
}

// Story 4.5 — batch reorder menu items within a single scope.
export interface ReorderMenuItemPayload {
  id: string
  order: number
}

interface ReorderContext {
  previousLists: Array<[readonly unknown[], unknown]>
}

export function useReorderMenusMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation<void, Error, ReorderMenuItemPayload[], ReorderContext>({
    mutationFn: (items) =>
      httpClient.put<void>('/api/admin/menus/reorder', { items }),
    // Optimistic update per architecture line 846 ("Optimistic only for: theme
    // change, soft-delete row removal, drag-reorder"). Snapshot every cached
    // list so onError can roll back.
    onMutate: async (items) => {
      await queryClient.cancelQueries({ queryKey: MENUS_ADMIN_QUERY_KEY })
      const previousLists = queryClient.getQueriesData({ queryKey: MENUS_ADMIN_QUERY_KEY })
      const idToOrder = new Map(items.map((i) => [i.id, i.order]))
      for (const [key, cached] of previousLists) {
        if (cached == null) continue
        const paged = cached as PagedResult<MenuListItem>
        if (!Array.isArray(paged.data)) continue
        const next: PagedResult<MenuListItem> = {
          ...paged,
          data: [...paged.data]
            .map((m) => (idToOrder.has(m.id) ? { ...m, order: idToOrder.get(m.id)! } : m))
            .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id)),
        }
        queryClient.setQueryData(key, next)
      }
      return { previousLists }
    },
    onError: (err, _vars, context) => {
      if (context?.previousLists) {
        for (const [key, value] of context.previousLists) {
          queryClient.setQueryData(key, value)
        }
      }
      const code = err instanceof ApiError ? err.code : null
      switch (code) {
        case 'MENUS_NOT_FOUND':
          toast.error(t('admin.menus.reorderMenusNotFound'))
          break
        case 'REORDER_MIXED_SCOPES':
          toast.error(t('admin.menus.reorderMixedScopes'))
          break
        case 'REORDER_CONFLICT':
          toast.error(t('admin.menus.reorderConflict'))
          break
        default:
          toast.error(t('admin.menus.reorderError'))
      }
    },
    onSuccess: () => {
      toast.success(t('admin.menus.reorderSuccess'))
    },
    // Always re-sync from the server so any tie-break differences between the
    // client's optimistic sort and the server's ORDER BY sort_order, id are
    // corrected. Without this, the cache could drift on the rare collision.
    onSettled: () => {
      invalidateAllMenus(queryClient)
    },
  })
}

// Story 5.2 — bind a Published Designer version to a menu item. Pessimistic
// (provisioning is asynchronous; the server returns 202 immediately and the
// detail query's refetchInterval-driven polling then observes the Pending →
// Success | Error transition). On success we invalidate the detail query so
// the new Pending state shows up immediately; the list query is also bumped
// because the (currently inactive) list could show a "bound" badge in future.
export interface BindMenuDesignerPayload {
  menuId: string
  designerId: string
  version: number
}

export function useBindDesignerMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: ({ menuId, designerId, version }: BindMenuDesignerPayload) =>
      httpClient.put<void>(`/api/admin/menus/${menuId}/binding`, { designerId, version }),
    onSuccess: (_data, { menuId }) => {
      void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
      invalidateAllMenus(queryClient)
      toast.success(t('admin.menus.bindSuccess'))
    },
    onError: (err) => {
      const code = err instanceof ApiError ? err.code : null
      switch (code) {
        case 'MENU_NOT_FOUND':
          toast.error(t('admin.menus.notFoundError'))
          break
        case 'DESIGNER_VERSION_NOT_FOUND':
          toast.error(t('admin.menus.designerVersionNotFound'))
          break
        case 'VERSION_NOT_PUBLISHED':
          toast.error(t('designers.versionNotPublished'))
          break
        default:
          toast.error(t('admin.menus.bindError'))
      }
    },
  })
}

// Story 5.2 — re-enqueue provisioning for an existing binding. Resets server
// status to Pending; the detail query's polling picks the transition back up.
export function useRetryBindingMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (menuId: string) =>
      httpClient.post<void>(`/api/admin/menus/${menuId}/binding/retry`, {}),
    onSuccess: (_data, menuId) => {
      void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
      invalidateAllMenus(queryClient)
      toast.success(t('admin.menus.retrySuccess'))
    },
    onError: (err) => {
      const code = err instanceof ApiError ? err.code : null
      switch (code) {
        case 'MENU_NOT_FOUND':
          toast.error(t('admin.menus.notFoundError'))
          break
        case 'MENU_NO_BINDING':
          toast.error(t('admin.menus.noBinding'))
          break
        default:
          toast.error(t('admin.menus.retryError'))
      }
    },
  })
}

// Set or clear a menu's custom route path — the alternative to a Designer binding.
// Pessimistic: the server clears any existing binding and busts the navbar cache, so
// we invalidate the detail query (the binding section re-reads routePath/designerId)
// and the list. Passing null clears the route.
export function useSetRoutePathMutation(menuId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (routePath: string | null) =>
      httpClient.put<void>(`/api/admin/menus/${menuId}/route-path`, { routePath }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
      invalidateAllMenus(queryClient)
      toast.success(t('admin.menus.routePathSuccess'))
    },
    onError: (err) => {
      const code = err instanceof ApiError ? err.code : null
      if (code === 'MENU_NOT_FOUND') {
        toast.error(t('admin.menus.notFoundError'))
        return
      }
      toast.error(t('admin.menus.routePathError'))
    },
  })
}

// Story 4.6 — pessimistic isActive toggle. Not in the architecture's optimistic
// allowlist (theme / soft-delete row removal / drag-reorder). Invalidates the
// list on success so the UI reflects the new state.
export function useToggleMenuActiveMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: ({ menuId, isActive }: { menuId: string; isActive: boolean }) =>
      httpClient.patch<void>(`/api/admin/menus/${menuId}/active`, { isActive }),
    onSuccess: (_data, { isActive }) => {
      invalidateAllMenus(queryClient)
      toast.success(isActive ? t('admin.menus.activateSuccess') : t('admin.menus.deactivateSuccess'))
    },
    onError: (err) => {
      const code = err instanceof ApiError ? err.code : null
      if (code === 'MENU_NOT_FOUND') {
        invalidateAllMenus(queryClient)
        toast.error(t('admin.menus.notFoundError'))
        return
      }
      toast.error(t('admin.menus.toggleActiveError'))
    },
  })
}
