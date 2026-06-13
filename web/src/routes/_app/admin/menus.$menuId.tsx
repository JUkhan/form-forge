import { useState, useEffect, useRef } from 'react'
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ArrowLeft, ChevronUp } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import { useMenuDetailQuery } from '../../../features/admin/menus/useMenuDetailQuery'
import { useMenuChildrenQuery } from '../../../features/admin/menus/useMenuChildrenQuery'
import {
  useAssignMenuRolesMutation,
  useCreateMenuMutation,
  useDeleteMenuMutation,
  useToggleMenuActiveMutation,
  useUpdateMenuMutation,
} from '../../../features/admin/menus/menuAdminMutations'
import { ReorderableMenuList } from '../../../features/admin/menus/ReorderableMenuList'
import { DesignerBindingSection } from '../../../features/admin/menus/DesignerBindingSection'
import { IconPickerSection } from '../../../features/admin/menus/IconPickerSection'
import { useRolesQuery } from '../../../features/admin/roles/useRolesQuery'
import { ApiError } from '../../../lib/api/apiError'
import type { MenuIcon, ProvisioningStatus } from '../../../features/menu/types'
import { usePollProvisioning } from '../../../features/menu/usePollProvisioning'

export const Route = createFileRoute('/_app/admin/menus/$menuId')({
  component: MenuDetailPage,
})

const updateMenuSchema = z.object({
  name: z.string().min(1, { message: 'admin.menus.nameRequired' }).max(200, { message: 'admin.menus.nameMaxLength' }),
  order: z.number().int().min(0),
  isActive: z.boolean(),
})
type UpdateMenuFormValues = z.infer<typeof updateMenuSchema>

function MenuDetailPage() {
  const { t } = useTranslation()
  const { menuId } = Route.useParams()
  const menuQuery = useMenuDetailQuery(menuId)

  if (menuQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">{t('admin.menus.loading')}</p>
  }
  if (menuQuery.isError || !menuQuery.data) {
    return (
      <div className="space-y-3">
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.menus.notFound')}
        </div>
        <Link to="/admin/menus" className="inline-flex items-center gap-1 text-sm text-primary hover:underline">
          <ArrowLeft className="h-4 w-4" />
          {t('admin.menus.backToMenus')}
        </Link>
      </div>
    )
  }

  return <MenuDetailContent menu={menuQuery.data} menuId={menuId} />
}

interface MenuDetailContentProps {
  menu: {
    id: string
    name: string
    order: number
    isActive: boolean
    parentId: string | null
    icon: MenuIcon | null
    allowedRoleIds: string[]
    designerId: string | null
    boundVersion: number | null
    provisioningStatus: ProvisioningStatus | null
    provisioningError: string | null
    routePath: string | null
  }
  menuId: string
}

function MenuDetailContent({ menu, menuId }: MenuDetailContentProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const updateMutation = useUpdateMenuMutation(menuId)
  const deleteMutation = useDeleteMenuMutation()
  usePollProvisioning(menu.provisioningStatus, menu.provisioningError)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [pendingIcon, setPendingIcon] = useState<MenuIcon | null>(menu.icon)
  // P6: re-sync pendingIcon when the query refetches fresh data (e.g. after a successful
  // save or background stale-while-revalidate), so concurrent edits don't get silently
  // overwritten by a stale local value on the next submit.
  useEffect(() => {
    setPendingIcon(menu.icon)
  }, [menu.icon])

  const {
    register,
    handleSubmit,
    formState: { errors },
    setError,
  } = useForm<UpdateMenuFormValues>({
    resolver: zodResolver(updateMenuSchema),
    mode: 'onChange',
    defaultValues: { name: menu.name, order: menu.order, isActive: menu.isActive },
  })

  const submit = async (values: UpdateMenuFormValues) => {
    try {
      await updateMutation.mutateAsync({
        name: values.name,
        order: values.order,
        icon: pendingIcon,
        isActive: values.isActive,
      })
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setError('root', { message: t('admin.menus.notFoundError') })
        return
      }
      setError('root', { message: t('admin.menus.saveError') })
    }
  }

  const onDelete = async () => {
    setDeleteError(null)
    try {
      await deleteMutation.mutateAsync(menuId)
      void navigate({ to: '/admin/menus' })
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'MENU_HAS_CHILDREN') {
        setDeleteError(t('admin.menus.hasChildren'))
        return
      }
      if (err instanceof ApiError && err.status === 404) {
        setDeleteError(t('admin.menus.notFoundError'))
        return
      }
      setDeleteError(t('admin.menus.deleteError'))
    }
  }

  const isSaving = updateMutation.isPending
  const isDeleting = deleteMutation.isPending
  const isTopLevel = !menu.parentId

  return (
    <div className="space-y-6">
      <div className="min-w-0">
        <Link
          to="/admin/menus"
          className="mb-2 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3.5 w-3.5" />
          {t('admin.menus.backToMenus')}
        </Link>
        <div className="flex flex-wrap items-baseline gap-3">
          <h1 className="text-2xl font-bold tracking-tight">{t('admin.menus.detailTitle')}</h1>
          {!isTopLevel && menu.parentId && (
            <Link
              to="/admin/menus/$menuId"
              params={{ menuId: menu.parentId }}
              className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
            >
              <ChevronUp className="h-3.5 w-3.5" />
              {t('admin.menus.viewParent')}
            </Link>
          )}
        </div>
      </div>

      <form
        onSubmit={(e) => {
          void handleSubmit(submit)(e)
        }}
        className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
      >
        <div className="space-y-1.5">
          <Label htmlFor="edit-menu-name">{t('admin.menus.nameLabel')}</Label>
          <Input id="edit-menu-name" type="text" {...register('name')} />
          {errors.name && (
            <p role="alert" className="text-xs text-destructive">
              {t(errors.name.message ?? 'admin.menus.nameRequired')}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="edit-menu-order">{t('admin.menus.orderLabel')}</Label>
          <Input
            id="edit-menu-order"
            type="number"
            min={0}
            {...register('order', { valueAsNumber: true })}
          />
          <p className="text-xs text-muted-foreground">{t('admin.menus.orderHelp')}</p>
        </div>

        <div className="space-y-1.5">
          <Label className="cursor-pointer">
            <input type="checkbox" {...register('isActive')} className="h-4 w-4 rounded border-field-border" />
            {t('admin.menus.isActiveLabel')}
          </Label>
          <p className="text-xs text-muted-foreground">{t('admin.menus.isActiveHelp')}</p>
        </div>

        <IconPickerSection icon={pendingIcon} onChange={setPendingIcon} />

        {errors.root && (
          <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
            {errors.root.message}
          </div>
        )}

        <Button type="submit" disabled={isSaving}>
          {isSaving ? t('admin.menus.savingButton') : t('admin.menus.saveButton')}
        </Button>
      </form>

      <div className="space-y-2">
        <Button
          type="button"
          variant="destructive"
          onClick={() => {
            void onDelete()
          }}
          disabled={isDeleting}
        >
          {isDeleting ? t('admin.menus.deletingButton') : t('admin.menus.deleteButton')}
        </Button>
        {deleteError && (
          <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
            {deleteError}
          </div>
        )}
      </div>

      {isTopLevel ? (
        <SubMenusSection parentId={menuId} />
      ) : (
        // Backend enforces max depth = 2 (MenuService.CreateMenuAsync rejects a
        // sub-sub-menu with MAX_MENU_DEPTH_EXCEEDED). Without this note the
        // missing "Sub-Menus" section looks like a bug.
        <SubMenuLeafNote parentId={menu.parentId} />
      )}

      {/* Remount when the role-membership changes — keeps the local Set draft
          aligned with server state without setState-in-effect. */}
      <RoleAssignmentSection
        key={menu.allowedRoleIds.slice().sort().join(',')}
        currentRoleIds={menu.allowedRoleIds}
        menuId={menuId}
      />

      <DesignerBindingSection
        menuId={menuId}
        designerId={menu.designerId}
        boundVersion={menu.boundVersion}
        provisioningStatus={menu.provisioningStatus}
        provisioningError={menu.provisioningError}
        routePath={menu.routePath}
      />
    </div>
  )
}

// Stand-in for SubMenusSection when the current menu is a sub-menu.
function SubMenuLeafNote({ parentId }: { parentId: string | null }) {
  const { t } = useTranslation()
  const parentQuery = useMenuDetailQuery(parentId ?? '')
  if (parentId === null) return null
  const parentName = parentQuery.data?.name ?? parentId
  return (
    <aside className="rounded-lg border border-dashed border-border bg-muted/50 px-4 py-3 text-sm text-muted-foreground">
      {t('admin.menus.isSubMenuNote', { parent: parentName })}
    </aside>
  )
}

interface SubMenusSectionProps {
  parentId: string
}

const CHILDREN_PAGE_SIZE = 25

function SubMenusSection({ parentId }: SubMenusSectionProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [mode, setMode] = useState<'manage' | 'reorder'>('manage')
  const childrenQuery = useMenuChildrenQuery(parentId, page, CHILDREN_PAGE_SIZE)
  const createMutation = useCreateMenuMutation()
  const toggleActiveMutation = useToggleMenuActiveMutation()

  const [subName, setSubName] = useState('')
  const [subOrder, setSubOrder] = useState(0)
  const [subCreateError, setSubCreateError] = useState<string | null>(null)

  const childMenus = childrenQuery.data?.data ?? []
  const totalPages = childrenQuery.data?.totalPages ?? 1
  const hasPrev = page > 1
  const hasNext = page < totalPages

  const onSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    setSubCreateError(null)
    const trimmed = subName.trim()
    if (trimmed.length === 0) {
      setSubCreateError(t('admin.menus.nameRequired'))
      return
    }
    try {
      await createMutation.mutateAsync({ name: trimmed, order: subOrder, parentId })
      setSubName('')
      setSubOrder(0)
    } catch (err) {
      if (err instanceof ApiError && err.status === 422 && err.code === 'MENU_PARENT_NOT_FOUND') {
        setSubCreateError(t('admin.menus.parentNotFound'))
        return
      }
      if (err instanceof ApiError && err.status === 422 && err.code === 'MAX_MENU_DEPTH_EXCEEDED') {
        setSubCreateError(t('admin.menus.maxDepthExceeded'))
        return
      }
      setSubCreateError(t('admin.menus.saveError'))
    }
  }

  const isCreating = createMutation.isPending

  return (
    <section className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <div className="flex items-baseline justify-between">
        <h2 className="text-lg font-semibold text-foreground">{t('admin.menus.subMenusTitle')}</h2>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => {
            setMode((m) => (m === 'manage' ? 'reorder' : 'manage'))
          }}
        >
          {mode === 'manage' ? t('admin.menus.reorderModeButton') : t('admin.menus.manageModeButton')}
        </Button>
      </div>

      {mode === 'reorder' && <ReorderableMenuList parentId={parentId} />}

      {mode === 'manage' && childrenQuery.isLoading && (
        <p className="text-sm text-muted-foreground">{t('admin.menus.loading')}</p>
      )}

      {mode === 'manage' && !childrenQuery.isLoading && childMenus.length === 0 && (
        <p className="text-sm text-muted-foreground">{t('admin.menus.noSubMenus')}</p>
      )}

      {mode === 'manage' && childMenus.length > 0 && (
        <>
          <div className="overflow-hidden rounded-lg border border-border">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                  <th className="px-4 py-2.5">{t('admin.menus.nameLabel')}</th>
                  <th className="px-4 py-2.5">{t('admin.menus.orderLabel')}</th>
                  <th className="px-4 py-2.5">{t('admin.menus.isActiveLabel')}</th>
                  <th className="px-4 py-2.5 text-right">{t('admin.menus.actionsLabel')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {childMenus.map((child) => (
                  <tr key={child.id} className="hover:bg-overlay-hover">
                    <td className="px-4 py-2.5">
                      <Link
                        to="/admin/menus/$menuId"
                        params={{ menuId: child.id }}
                        className="font-medium text-primary hover:underline"
                      >
                        {child.name}
                      </Link>
                    </td>
                    <td className="px-4 py-2.5 text-muted-foreground">{child.order}</td>
                    <td className="px-4 py-2.5">
                      <span
                        className={cn(
                          'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium',
                          child.isActive
                            ? 'bg-emerald-100 text-emerald-800'
                            : 'bg-muted text-muted-foreground',
                        )}
                      >
                        {child.isActive
                          ? t('admin.menus.isActiveLabel')
                          : t('admin.menus.isInactiveLabel')}
                      </span>
                    </td>
                    <td className="px-4 py-2.5 text-right">
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={() =>
                          toggleActiveMutation.mutate({ menuId: child.id, isActive: !child.isActive })
                        }
                        disabled={toggleActiveMutation.isPending}
                        aria-label={
                          child.isActive
                            ? t('admin.menus.deactivateButtonAria', { name: child.name })
                            : t('admin.menus.activateButtonAria', { name: child.name })
                        }
                      >
                        {child.isActive
                          ? t('admin.menus.deactivateButton')
                          : t('admin.menus.activateButton')}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {totalPages > 1 && (
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={!hasPrev}
                onClick={() => {
                  setPage((p) => Math.max(1, p - 1))
                }}
              >
                {t('admin.menus.previousPage')}
              </Button>
              <span className="text-xs text-muted-foreground">
                {t('admin.menus.pageIndicator', { page, totalPages })}
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={!hasNext}
                onClick={() => {
                  setPage((p) => p + 1)
                }}
              >
                {t('admin.menus.nextPage')}
              </Button>
            </div>
          )}
        </>
      )}

      {mode === 'manage' && (
        <form
          onSubmit={(e) => {
            void onSubmit(e)
          }}
          className="space-y-3 border-t border-border pt-4"
        >
          <h3 className="text-sm font-semibold text-foreground">{t('admin.menus.addSubMenuButton')}</h3>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="sub-menu-name">{t('admin.menus.nameLabel')}</Label>
              <Input
                id="sub-menu-name"
                type="text"
                value={subName}
                onChange={(e) => {
                  setSubName(e.target.value)
                }}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="sub-menu-order">{t('admin.menus.orderLabel')}</Label>
              <Input
                id="sub-menu-order"
                type="number"
                min={0}
                value={subOrder}
                onChange={(e) => {
                  const n = Number(e.target.value)
                  setSubOrder(Number.isFinite(n) ? n : 0)
                }}
              />
            </div>
          </div>
          {subCreateError && (
            <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {subCreateError}
            </div>
          )}
          <Button type="submit" disabled={isCreating}>
            {isCreating ? t('admin.menus.addingSubMenuButton') : t('admin.menus.addSubMenuButton')}
          </Button>
        </form>
      )}
    </section>
  )
}

interface RoleAssignmentSectionProps {
  currentRoleIds: string[]
  menuId: string
}

function RoleAssignmentSection({ currentRoleIds, menuId }: RoleAssignmentSectionProps) {
  const { t } = useTranslation()
  const rolesQuery = useRolesQuery()
  const assignMutation = useAssignMenuRolesMutation(menuId)

  const [selected, setSelected] = useState<Set<string>>(() => new Set(currentRoleIds))
  const [assignError, setAssignError] = useState<string | null>(null)
  // Empty-list saves clear every assignment (AC-4) and hide the menu from all
  // users. Require a two-click confirmation within 3 s to commit; a timer
  // auto-cancels the armed state so a stale warning never lingers. (Story 4.4
  // bmad review P8.)
  const [emptyConfirmArmed, setEmptyConfirmArmed] = useState(false)
  const emptyConfirmTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const cancelEmptyConfirm = () => {
    if (emptyConfirmTimerRef.current !== null) {
      clearTimeout(emptyConfirmTimerRef.current)
      emptyConfirmTimerRef.current = null
    }
    setEmptyConfirmArmed(false)
  }

  useEffect(() => {
    return () => {
      if (emptyConfirmTimerRef.current !== null) {
        clearTimeout(emptyConfirmTimerRef.current)
      }
    }
  }, [])

  const toggle = (roleId: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(roleId)) next.delete(roleId)
      else next.add(roleId)
      return next
    })
    cancelEmptyConfirm()
  }

  const save = async () => {
    setAssignError(null)
    try {
      await assignMutation.mutateAsync({ roleIds: Array.from(selected) })
      cancelEmptyConfirm()
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setAssignError(t('admin.menus.notFoundError'))
        return
      }
      if (err instanceof ApiError && err.code === 'ASSIGNMENT_CONFLICT') {
        setAssignError(t('admin.menus.assignmentConflict'))
        return
      }
      setAssignError(t('admin.menus.rolesAssignError'))
    }
  }

  const handleSaveClick = () => {
    if (selected.size === 0) {
      if (emptyConfirmArmed) {
        cancelEmptyConfirm()
        void save()
        return
      }
      setEmptyConfirmArmed(true)
      emptyConfirmTimerRef.current = setTimeout(() => {
        emptyConfirmTimerRef.current = null
        setEmptyConfirmArmed(false)
      }, 3000)
      return
    }
    void save()
  }

  const isSaving = assignMutation.isPending
  const availableRoles = rolesQuery.data?.data ?? []

  // Merge previously-assigned roles that aren't in the paginated catalog so a
  // full-sync save can never silently drop them. (Story 4.4 bmad review P9.)
  const visibleRoleIds = new Set(availableRoles.map((r) => r.id))
  const hiddenAssignedIds = currentRoleIds.filter((id) => !visibleRoleIds.has(id))

  const isCatalogEmpty = availableRoles.length === 0 && hiddenAssignedIds.length === 0
  const isCatalogCapped = availableRoles.length === 100
  const saveDisabled =
    isSaving || rolesQuery.isLoading || rolesQuery.isError || isCatalogEmpty

  return (
    <section className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <div>
        <h2 className="text-lg font-semibold text-foreground">
          {t('admin.menus.rolesSectionTitle')}
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">{t('admin.menus.rolesHelp')}</p>
      </div>
      {rolesQuery.isLoading && <p className="text-sm text-muted-foreground">{t('admin.menus.loading')}</p>}
      {!rolesQuery.isLoading && isCatalogEmpty && (
        <p className="text-sm text-muted-foreground">{t('admin.menus.rolesEmpty')}</p>
      )}
      {isCatalogCapped && (
        <p className="text-xs text-muted-foreground">{t('admin.menus.rolesCatalogCapped')}</p>
      )}
      <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        {availableRoles.map((role) => (
          <li key={role.id}>
            <label className="flex cursor-pointer items-center gap-2 rounded-md border border-border px-3 py-2 text-sm transition-colors hover:bg-overlay-hover active:bg-overlay-active">
              <input
                type="checkbox"
                checked={selected.has(role.id)}
                onChange={() => {
                  toggle(role.id)
                }}
                className="h-4 w-4 rounded border-field-border"
              />
              <span>{role.name}</span>
            </label>
          </li>
        ))}
        {hiddenAssignedIds.map((id) => (
          <li key={id}>
            <label className="flex cursor-pointer items-center gap-2 rounded-md border border-border px-3 py-2 text-sm transition-colors hover:bg-overlay-hover active:bg-overlay-active">
              <input
                type="checkbox"
                checked={selected.has(id)}
                onChange={() => {
                  toggle(id)
                }}
                className="h-4 w-4 rounded border-field-border"
              />
              <span className="italic text-muted-foreground">{t('admin.menus.unknownRole', { id })}</span>
            </label>
          </li>
        ))}
      </ul>
      {emptyConfirmArmed && (
        <div
          role="alert"
          className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
        >
          {t('admin.menus.rolesEmptyWarning')}
        </div>
      )}
      {assignError && (
        <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {assignError}
        </div>
      )}
      <Button type="button" onClick={handleSaveClick} disabled={saveDisabled}>
        {isSaving ? t('admin.menus.savingRolesButton') : t('admin.menus.saveRolesButton')}
      </Button>
    </section>
  )
}
