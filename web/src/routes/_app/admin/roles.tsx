import { useCallback, useState } from 'react'
import { createFileRoute, Link, Outlet, useMatchRoute, useNavigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Shield, FolderOpen } from 'lucide-react'
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
import { useRolesQueryPaginated } from '../../../features/admin/roles/useRolesQueryPaginated'
import { useCreateRoleMutation } from '../../../features/admin/roles/roleMutations'
import { ApiError } from '../../../lib/api/apiError'

const rolesSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  sort: z.string().optional(),
  q: z.string().optional(),
  system: z.enum(['system', 'custom']).optional(),
})

export const Route = createFileRoute('/_app/admin/roles')({
  validateSearch: rolesSearchSchema,
  component: RolesPage,
})

// Mirror the server-side FluentValidation regex on CreateRoleRequestValidator.
// Catching the format constraint client-side prevents a 422 round-trip whose
// FluentValidation message is hidden by the generic error fallback.
const ROLE_NAME_REGEX = /^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$/

const createRoleSchema = z.object({
  name: z
    .string()
    .min(1)
    .max(100)
    .regex(
      ROLE_NAME_REGEX,
      'Use lowercase letters, digits, and hyphens only (must start with a letter; no leading/trailing hyphen).',
    )
    .refine((name) => !name.includes('--'), 'Role name cannot contain consecutive hyphens.'),
  description: z.string().max(500).optional().or(z.literal('')),
})
type CreateRoleFormValues = z.infer<typeof createRoleSchema>

function RolesPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/roles' })
  const matchRoute = useMatchRoute()
  const { page, pageSize, sort, q, system } = Route.useSearch()
  const rolesQuery = useRolesQueryPaginated(page, pageSize, { sort, search: q, system })
  const [showCreate, setShowCreate] = useState(false)

  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  const setSort = useCallback(
    (next: string | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, sort: next, page: 1 }) }),
    [navigate],
  )
  const setSystem = useCallback(
    (next: 'system' | 'custom' | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, system: next, page: 1 }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )
  const clearFilters = useCallback(
    () => void navigate({ search: (prev) => ({ ...prev, q: undefined, system: undefined, page: 1 }) }),
    [navigate],
  )

  // See routing_dot_nested_parent_layouts memory: roles.tsx is a layout parent
  // for roles.$roleId.tsx, so without an <Outlet /> the detail page is matched
  // but never rendered. Hooks must come before this early-return.
  const isRoleDetailActive = !!matchRoute({
    to: '/admin/roles/$roleId',
    fuzzy: false,
  })
  if (isRoleDetailActive) {
    return <Outlet />
  }

  const totalPages = Math.max(1, rolesQuery.data?.totalPages ?? 1)
  const clampedPage = Math.min(page, totalPages)
  const hasFilters = !!q || !!system
  const showEmpty = !!rolesQuery.data && rolesQuery.data.data.length === 0

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <Shield className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('admin.roles.title')}</h1>
          </div>
          <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">{t('admin.roles.subtitle')}</p>
        </div>
        <Button onClick={() => setShowCreate((s) => !s)}>
          {t('admin.roles.createButton')}
        </Button>
      </div>

      {showCreate && <CreateRoleForm onDone={() => setShowCreate(false)} />}

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <SearchBox value={q ?? ''} onChange={setSearch} placeholder={t('admin.roles.searchPlaceholder')} />
        <div className="flex items-center gap-2">
          <Select
            value={system ?? 'all'}
            onValueChange={(v) => setSystem(v === 'all' ? undefined : (v as 'system' | 'custom'))}
          >
            <SelectTrigger className="h-9 w-40" aria-label={t('admin.roles.filterTypeLabel')}>
              <SelectValue placeholder={t('admin.roles.filterTypeAll')} />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t('admin.roles.filterTypeAll')}</SelectItem>
              <SelectItem value="system">{t('admin.roles.filterTypeSystem')}</SelectItem>
              <SelectItem value="custom">{t('admin.roles.filterTypeCustom')}</SelectItem>
            </SelectContent>
          </Select>
          {hasFilters && (
            <Button variant="ghost" size="sm" onClick={clearFilters}>
              {t('common.clearFilters')}
            </Button>
          )}
        </div>
      </div>

      {rolesQuery.isError && (
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.roles.loadError')}
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-5 py-3">
                  <SortHeader colKey="name" label={t('admin.roles.nameLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="description" label={t('admin.roles.descriptionLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="permissionCount" label={t('admin.roles.permissionCountLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3 text-right">
                  <SortHeader colKey="system" label={t('admin.roles.typeColumnLabel')} sort={sort} onSort={setSort} className="ml-auto" />
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {rolesQuery.isLoading && (
                <tr>
                  <td colSpan={4} className="px-5 py-8 text-center text-sm text-muted-foreground">
                    {t('admin.roles.loading')}
                  </td>
                </tr>
              )}
              {showEmpty && (
                <tr>
                  <td colSpan={4} className="px-5 py-16 text-center">
                    <div className="flex flex-col items-center gap-3">
                      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                        <FolderOpen className="h-6 w-6" />
                      </div>
                      {hasFilters ? (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.roles.noMatches')}</p>
                          <Button variant="outline" onClick={clearFilters}>
                            {t('common.clearFilters')}
                          </Button>
                        </>
                      ) : (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.roles.noRoles')}</p>
                          <Button onClick={() => setShowCreate(true)}>
                            {t('admin.roles.createButton')}
                          </Button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              )}
              {rolesQuery.data?.data.map((role) => (
                <tr key={role.id} className="hover:bg-overlay-hover">
                  <td className="px-5 py-3">
                    <Link
                      to="/admin/roles/$roleId"
                      params={{ roleId: role.id }}
                      className="font-medium text-primary hover:underline"
                    >
                      {role.name}
                    </Link>
                  </td>
                  <td className="px-5 py-3 text-muted-foreground">{role.description ?? ''}</td>
                  <td className="px-5 py-3 text-muted-foreground">{role.permissionCount}</td>
                  <td className="px-5 py-3 text-right">
                    {role.isSystem && <SystemBadge />}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {!!rolesQuery.data && rolesQuery.data.data.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page - 1)}
            disabled={page <= 1 || rolesQuery.isFetching}
          >
            {t('admin.roles.previousPage')}
          </Button>
          <span>
            {t('admin.roles.pageIndicator', { page: clampedPage, totalPages })}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page + 1)}
            disabled={page >= totalPages || rolesQuery.isFetching}
          >
            {t('admin.roles.nextPage')}
          </Button>
        </nav>
      )}
    </div>
  )
}

function SystemBadge() {
  const { t } = useTranslation()
  return (
    <span className="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-1 text-xs font-medium text-indigo-800">
      {t('admin.roles.systemBadge')}
    </span>
  )
}

interface CreateRoleFormProps {
  onDone: () => void
}

function CreateRoleForm({ onDone }: CreateRoleFormProps) {
  const { t } = useTranslation()
  const createMutation = useCreateRoleMutation()
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateRoleFormValues>({
    resolver: zodResolver(createRoleSchema),
    mode: 'onChange',
  })

  const onSubmit = async (values: CreateRoleFormValues) => {
    try {
      await createMutation.mutateAsync({
        name: values.name,
        description: values.description?.trim() ? values.description.trim() : null,
        // Matrix is empty at create time — admin populates it after Menu
        // Bindings exist (Epic 4) and rows appear on the detail page.
        permissions: [],
      })
      reset()
      onDone()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'ROLE_NAME_CONFLICT') {
        setError('name', { type: 'server', message: t('admin.roles.nameConflict') })
        return
      }
      setError('root', { message: t('errors.genericError') })
    }
  }

  return (
    <form
      onSubmit={(e) => {
        void handleSubmit(onSubmit)(e)
      }}
      aria-label={t('admin.roles.createDialogTitle')}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">{t('admin.roles.createDialogTitle')}</h2>

      <div className="space-y-1.5">
        <Label htmlFor="newrole-name">{t('admin.roles.nameLabel')}</Label>
        <Input id="newrole-name" type="text" autoComplete="off" {...register('name')} />
        {errors.name && (
          <p role="alert" className="text-xs text-destructive">
            {errors.name.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newrole-description">{t('admin.roles.descriptionLabel')}</Label>
        <Input id="newrole-description" type="text" autoComplete="off" {...register('description')} />
        {errors.description && (
          <p role="alert" className="text-xs text-destructive">
            {errors.description.message}
          </p>
        )}
      </div>

      {errors.root && (
        <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {errors.root.message}
        </div>
      )}

      <div className="flex gap-2">
        <Button type="submit" disabled={isSubmitting}>
          {t('admin.roles.saveButton')}
        </Button>
        <Button type="button" variant="outline" onClick={onDone}>
          {t('admin.roles.cancelButton')}
        </Button>
      </div>
    </form>
  )
}
