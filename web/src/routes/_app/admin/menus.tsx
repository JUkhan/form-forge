import { useCallback, useMemo, useState } from 'react'
import { createFileRoute, Link, Outlet, useMatchRoute, useNavigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Menu as MenuIconLucide, FolderOpen } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SortHeader } from '@/components/shared/SortHeader'
import { SearchBox } from '@/components/shared/SearchBox'
import { cn } from '@/lib/utils'
import { useMenusAdminQuery } from '../../../features/admin/menus/useMenusAdminQuery'
import {
  useCreateMenuMutation,
  useToggleMenuActiveMutation,
} from '../../../features/admin/menus/menuAdminMutations'
import { ReorderableMenuList } from '../../../features/admin/menus/ReorderableMenuList'
import { IconPickerSection } from '../../../features/admin/menus/IconPickerSection'
import { ApiError } from '../../../lib/api/apiError'
import type { MenuIcon, MenuListItem } from '../../../features/menu/types'

const menusSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  // Manage-mode only. `order` is never sortable — it's the manual reorder axis.
  sort: z.string().optional(),
  q: z.string().optional(),
  active: z.enum(['active', 'inactive']).optional(),
})

export const Route = createFileRoute('/_app/admin/menus')({
  validateSearch: menusSearchSchema,
  component: MenusPage,
})

const createMenuSchema = z.object({
  name: z.string().min(1, { message: 'admin.menus.nameRequired' }).max(200, { message: 'admin.menus.nameMaxLength' }),
  order: z.number().int().min(0),
  isActive: z.boolean(),
  // Empty string from the native <select> means "top-level"; we translate to
  // null before sending to the server (which treats null/omitted as top-level).
  parentId: z.string().optional(),
})
type CreateMenuFormValues = z.infer<typeof createMenuSchema>

// Cap for the "all top-level menus" lookup used by the Parent dropdown and the
// Parent-column name resolution. Backend enforces max depth = 2 so only
// top-level menus can be parents; >100 top-level menus is unusual.
const TOP_LEVEL_LOOKUP_PAGE_SIZE = 100

function MenusPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/menus' })
  const matchRoute = useMatchRoute()
  const { page, pageSize, sort, q, active } = Route.useSearch()
  const menusQuery = useMenusAdminQuery(page, pageSize, { sort, search: q, active })
  // No opts — the parent-name lookup must stay unfiltered/unsorted regardless of
  // the manage-table's current sort/filter.
  const lookupQuery = useMenusAdminQuery(1, TOP_LEVEL_LOOKUP_PAGE_SIZE)
  const topLevelMenus = useMemo<MenuListItem[]>(
    () => (lookupQuery.data?.data ?? []).filter((m) => m.parentId === null),
    [lookupQuery.data],
  )
  const parentNameById = useMemo(() => {
    const map = new Map<string, string>()
    for (const m of topLevelMenus) map.set(m.id, m.name)
    return map
  }, [topLevelMenus])

  const toggleActiveMutation = useToggleMenuActiveMutation()
  const [showCreate, setShowCreate] = useState(false)
  const [mode, setMode] = useState<'manage' | 'reorder'>('manage')

  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  const setSort = useCallback(
    (next: string | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, sort: next, page: 1 }) }),
    [navigate],
  )
  const setActive = useCallback(
    (next: 'active' | 'inactive' | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, active: next, page: 1 }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )
  const clearFilters = useCallback(
    () => void navigate({ search: (prev) => ({ ...prev, q: undefined, active: undefined, page: 1 }) }),
    [navigate],
  )

  // File-based routing makes menus.tsx the layout parent for menus.$menuId.tsx.
  // See routing_dot_nested_parent_layouts memory. Hooks must come first.
  const isMenuDetailActive = !!matchRoute({
    to: '/admin/menus/$menuId',
    fuzzy: false,
  })
  if (isMenuDetailActive) {
    return <Outlet />
  }

  const totalPages = Math.max(1, menusQuery.data?.totalPages ?? 1)
  const clampedPage = Math.min(page, totalPages)
  const hasFilters = !!q || !!active
  const showEmpty = !!menusQuery.data && menusQuery.data.data.length === 0

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <MenuIconLucide className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('admin.menus.title')}</h1>
          </div>
          <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">{t('admin.menus.subtitle')}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="outline"
            onClick={() => {
              setMode((m) => (m === 'manage' ? 'reorder' : 'manage'))
            }}
          >
            {mode === 'manage' ? t('admin.menus.reorderModeButton') : t('admin.menus.manageModeButton')}
          </Button>
          <Button onClick={() => setShowCreate((s) => !s)}>
            {t('admin.menus.createButton')}
          </Button>
        </div>
      </div>

      {mode === 'reorder' && (
        <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
          <ReorderableMenuList parentId={null} />
        </div>
      )}

      {showCreate && (
        <CreateMenuForm
          onDone={() => setShowCreate(false)}
          topLevelMenus={topLevelMenus}
        />
      )}

      {mode === 'manage' && (
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
          <SearchBox value={q ?? ''} onChange={setSearch} placeholder={t('admin.menus.searchPlaceholder')} />
          <div className="flex items-center gap-2">
            <Select
              value={active ?? 'all'}
              onValueChange={(v) => setActive(v === 'all' ? undefined : (v as 'active' | 'inactive'))}
            >
              <SelectTrigger className="h-9 w-40" aria-label={t('admin.menus.filterActiveLabel')}>
                <SelectValue placeholder={t('admin.menus.filterActiveAll')} />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">{t('admin.menus.filterActiveAll')}</SelectItem>
                <SelectItem value="active">{t('admin.menus.isActiveLabel')}</SelectItem>
                <SelectItem value="inactive">{t('admin.menus.isInactiveLabel')}</SelectItem>
              </SelectContent>
            </Select>
            {hasFilters && (
              <Button variant="ghost" size="sm" onClick={clearFilters}>
                {t('common.clearFilters')}
              </Button>
            )}
          </div>
        </div>
      )}

      {mode === 'manage' && menusQuery.isError && (
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.menus.loadError')}
        </div>
      )}

      {mode === 'manage' && (
        <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                  <th className="px-5 py-3">
                    <SortHeader colKey="name" label={t('admin.menus.nameLabel')} sort={sort} onSort={setSort} />
                  </th>
                  <th className="px-5 py-3">{t('admin.menus.parentColumnLabel')}</th>
                  <th className="px-5 py-3">{t('admin.menus.orderLabel')}</th>
                  <th className="px-5 py-3">
                    <SortHeader colKey="status" label={t('admin.menus.isActiveLabel')} sort={sort} onSort={setSort} />
                  </th>
                  <th className="px-5 py-3">
                    <SortHeader colKey="createdAt" label={t('admin.menus.createdAtLabel')} sort={sort} onSort={setSort} />
                  </th>
                  <th className="px-5 py-3 text-right">{t('admin.menus.actionsLabel')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {menusQuery.isLoading && (
                  <tr>
                    <td colSpan={6} className="px-5 py-8 text-center text-sm text-muted-foreground">
                      {t('admin.menus.loading')}
                    </td>
                  </tr>
                )}
                {showEmpty && (
                  <tr>
                    <td colSpan={6} className="px-5 py-16 text-center">
                      <div className="flex flex-col items-center gap-3">
                        <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                          <FolderOpen className="h-6 w-6" />
                        </div>
                        {hasFilters ? (
                          <>
                            <p className="font-semibold text-foreground">{t('admin.menus.noMatches')}</p>
                            <Button variant="outline" onClick={clearFilters}>
                              {t('common.clearFilters')}
                            </Button>
                          </>
                        ) : (
                          <>
                            <p className="font-semibold text-foreground">{t('admin.menus.noMenus')}</p>
                            <Button onClick={() => setShowCreate(true)}>
                              {t('admin.menus.createButton')}
                            </Button>
                          </>
                        )}
                      </div>
                    </td>
                  </tr>
                )}
                {menusQuery.data?.data.map((menu) => {
                  const parentName = menu.parentId
                    ? (parentNameById.get(menu.parentId) ?? menu.parentId)
                    : null
                  return (
                    <tr key={menu.id} className="hover:bg-overlay-hover">
                      <td className="px-5 py-3">
                        {menu.parentId !== null && (
                          <span aria-hidden className="mr-1 text-muted-foreground">
                            {t('admin.menus.subMenuRowPrefix')}
                          </span>
                        )}
                        <Link
                          to="/admin/menus/$menuId"
                          params={{ menuId: menu.id }}
                          className="font-medium text-primary hover:underline"
                        >
                          {menu.name}
                        </Link>
                      </td>
                      <td className={cn('px-5 py-3', parentName ? 'text-muted-foreground' : 'text-muted-foreground')}>
                        {parentName ?? t('admin.menus.parentTopLevel')}
                      </td>
                      <td className="px-5 py-3 text-muted-foreground">{menu.order}</td>
                      <td className="px-5 py-3">
                        <ActiveBadge isActive={menu.isActive} />
                      </td>
                      <td className="px-5 py-3 text-muted-foreground">
                        {new Date(menu.createdAt).toLocaleDateString()}
                      </td>
                      <td className="px-5 py-3 text-right">
                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() =>
                            toggleActiveMutation.mutate({ menuId: menu.id, isActive: !menu.isActive })
                          }
                          disabled={toggleActiveMutation.isPending}
                          aria-label={
                            menu.isActive
                              ? t('admin.menus.deactivateButtonAria', { name: menu.name })
                              : t('admin.menus.activateButtonAria', { name: menu.name })
                          }
                        >
                          {menu.isActive
                            ? t('admin.menus.deactivateButton')
                            : t('admin.menus.activateButton')}
                        </Button>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {mode === 'manage' && !!menusQuery.data && menusQuery.data.data.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page - 1)}
            disabled={page <= 1 || menusQuery.isFetching}
          >
            {t('admin.menus.previousPage')}
          </Button>
          <span>
            {t('admin.menus.pageIndicator', { page: clampedPage, totalPages })}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page + 1)}
            disabled={page >= totalPages || menusQuery.isFetching}
          >
            {t('admin.menus.nextPage')}
          </Button>
        </nav>
      )}
    </div>
  )
}

function ActiveBadge({ isActive }: { isActive: boolean }) {
  const { t } = useTranslation()
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium',
        isActive ? 'bg-emerald-100 text-emerald-800' : 'bg-muted text-muted-foreground',
      )}
    >
      <span
        aria-hidden
        className={cn('h-1.5 w-1.5 rounded-full', isActive ? 'bg-emerald-700' : 'bg-muted-foreground')}
      />
      {isActive ? t('admin.menus.isActiveLabel') : t('admin.menus.isInactiveLabel')}
    </span>
  )
}

interface CreateMenuFormProps {
  onDone: () => void
  topLevelMenus: MenuListItem[]
}

function CreateMenuForm({ onDone, topLevelMenus }: CreateMenuFormProps) {
  const { t } = useTranslation()
  const createMutation = useCreateMenuMutation()
  // Icon is form-local state (not in react-hook-form's tree because MenuIcon
  // is a discriminated union, awkward to validate via zod here).
  const [pendingIcon, setPendingIcon] = useState<MenuIcon | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateMenuFormValues>({
    resolver: zodResolver(createMenuSchema),
    mode: 'onChange',
    defaultValues: { order: 0, isActive: true, parentId: '' },
  })

  const onSubmit = async (values: CreateMenuFormValues) => {
    try {
      await createMutation.mutateAsync({
        name: values.name,
        order: values.order,
        isActive: values.isActive,
        icon: pendingIcon,
        // Empty string from the native <select> means "no parent" — translate
        // to null. Server treats null/omitted as top-level (Story 4.2).
        parentId: values.parentId && values.parentId.length > 0 ? values.parentId : null,
      })
      reset()
      setPendingIcon(null)
      onDone()
    } catch (err) {
      if (err instanceof ApiError && err.status === 422 && err.code === 'MAX_MENU_DEPTH_EXCEEDED') {
        setError('parentId', { message: t('admin.menus.maxDepthExceeded') })
        return
      }
      if (err instanceof ApiError && err.status === 422 && err.code === 'MENU_PARENT_NOT_FOUND') {
        setError('parentId', { message: t('admin.menus.parentNotFound') })
        return
      }
      if (err instanceof ApiError && (err.status === 400 || err.status === 422)) {
        setError('root', { message: t('admin.menus.saveError') })
        return
      }
      setError('root', { message: t('admin.menus.saveError') })
    }
  }

  return (
    <form
      onSubmit={(e) => {
        void handleSubmit(onSubmit)(e)
      }}
      aria-label={t('admin.menus.createDialogTitle')}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">{t('admin.menus.createDialogTitle')}</h2>

      <div className="space-y-1.5">
        <Label htmlFor="newmenu-name">{t('admin.menus.nameLabel')}</Label>
        <Input id="newmenu-name" type="text" autoComplete="off" {...register('name')} />
        {errors.name && (
          <p role="alert" className="text-xs text-destructive">
            {t(errors.name.message ?? 'admin.menus.nameRequired')}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newmenu-parent">{t('admin.menus.parentLabel')}</Label>
        <select
          id="newmenu-parent"
          {...register('parentId')}
          className="h-8 w-full rounded-lg border border-input bg-transparent px-2.5 py-1 text-sm transition-colors outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
        >
          <option value="">{t('admin.menus.parentTopLevel')}</option>
          {topLevelMenus.map((m) => (
            <option key={m.id} value={m.id}>
              {m.name}
            </option>
          ))}
        </select>
        <p className="text-xs text-muted-foreground">{t('admin.menus.parentHelp')}</p>
        {errors.parentId && (
          <p role="alert" className="text-xs text-destructive">
            {errors.parentId.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newmenu-order">{t('admin.menus.orderLabel')}</Label>
        <Input
          id="newmenu-order"
          type="number"
          min={0}
          {...register('order', { valueAsNumber: true })}
        />
        <p className="text-xs text-muted-foreground">{t('admin.menus.orderHelp')}</p>
      </div>

      <div>
        <Label className="cursor-pointer">
          <input type="checkbox" {...register('isActive')} className="h-4 w-4 rounded border-field-border" />
          {t('admin.menus.isActiveLabel')}
        </Label>
      </div>

      <IconPickerSection icon={pendingIcon} onChange={setPendingIcon} />

      {errors.root && (
        <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {errors.root.message}
        </div>
      )}

      <div className="flex gap-2">
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? t('admin.menus.savingButton') : t('admin.menus.saveButton')}
        </Button>
        <Button type="button" variant="outline" onClick={onDone}>
          {t('admin.menus.cancelButton')}
        </Button>
      </div>
    </form>
  )
}
