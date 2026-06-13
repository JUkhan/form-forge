import { useCallback, useState } from 'react'
import { createFileRoute, Link, Outlet, useMatchRoute, useNavigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Users as UsersIcon, FolderOpen } from 'lucide-react'
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
import { useUsersQuery } from '../../../features/admin/users/useUsersQuery'
import { useCreateUserMutation } from '../../../features/admin/users/userMutations'
import { ApiError } from '../../../lib/api/apiError'

const usersSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  sort: z.string().optional(),
  q: z.string().optional(),
  status: z.enum(['active', 'inactive']).optional(),
})

export const Route = createFileRoute('/_app/admin/users')({
  validateSearch: usersSearchSchema,
  component: UsersPage,
})

const createUserSchema = z.object({
  email: z.string().min(1).email().max(320),
  displayName: z.string().min(1).max(200),
  temporaryPassword: z.string().min(8),
})
type CreateUserFormValues = z.infer<typeof createUserSchema>

function UsersPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/users' })
  const matchRoute = useMatchRoute()
  const { page, pageSize, sort, q, status } = Route.useSearch()
  const usersQuery = useUsersQuery(page, pageSize, { sort, search: q, status })
  const [showCreate, setShowCreate] = useState(false)

  // navigate() is stable; useCallback keeps these handlers referentially stable
  // so SearchBox's debounce timer isn't reset on every parent render.
  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  const setSort = useCallback(
    (next: string | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, sort: next, page: 1 }) }),
    [navigate],
  )
  const setStatus = useCallback(
    (next: 'active' | 'inactive' | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, status: next, page: 1 }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )
  const clearFilters = useCallback(
    () => void navigate({ search: (prev) => ({ ...prev, q: undefined, status: undefined, page: 1 }) }),
    [navigate],
  )

  // See routing_dot_nested_parent_layouts memory: users.tsx is a layout parent
  // for users.$userId.tsx, so without an <Outlet /> the detail page is matched
  // but never rendered. Hooks must come before this early-return.
  const isUserDetailActive = !!matchRoute({
    to: '/admin/users/$userId',
    fuzzy: false,
  })
  if (isUserDetailActive) {
    return <Outlet />
  }

  const totalPages = Math.max(1, usersQuery.data?.totalPages ?? 1)
  const hasFilters = !!q || !!status
  const showEmpty = !!usersQuery.data && usersQuery.data.data.length === 0

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <UsersIcon className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('admin.users.title')}</h1>
          </div>
          <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">{t('admin.users.subtitle')}</p>
        </div>
        <Button onClick={() => setShowCreate((s) => !s)}>
          {t('admin.users.createButton')}
        </Button>
      </div>

      {showCreate && <CreateUserForm onDone={() => setShowCreate(false)} />}

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <SearchBox value={q ?? ''} onChange={setSearch} placeholder={t('admin.users.searchPlaceholder')} />
        <div className="flex items-center gap-2">
          <Select
            value={status ?? 'all'}
            onValueChange={(v) => setStatus(v === 'all' ? undefined : (v as 'active' | 'inactive'))}
          >
            <SelectTrigger className="h-9 w-40" aria-label={t('admin.users.filterStatusLabel')}>
              <SelectValue placeholder={t('common.allStatuses')} />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t('common.allStatuses')}</SelectItem>
              <SelectItem value="active">{t('admin.users.statusActive')}</SelectItem>
              <SelectItem value="inactive">{t('admin.users.statusInactive')}</SelectItem>
            </SelectContent>
          </Select>
          {hasFilters && (
            <Button variant="ghost" size="sm" onClick={clearFilters}>
              {t('common.clearFilters')}
            </Button>
          )}
        </div>
      </div>

      {usersQuery.isError && (
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.users.loadError')}
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-5 py-3">
                  <SortHeader colKey="displayName" label={t('admin.users.displayNameLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="email" label={t('admin.users.emailLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="status" label={t('admin.users.statusLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="roleCount" label={t('admin.users.roleCountLabel')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="createdAt" label={t('admin.users.createdAtLabel')} sort={sort} onSort={setSort} />
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {usersQuery.isLoading && (
                <tr>
                  <td colSpan={5} className="px-5 py-8 text-center text-sm text-muted-foreground">
                    {t('admin.users.loading')}
                  </td>
                </tr>
              )}
              {showEmpty && (
                <tr>
                  <td colSpan={5} className="px-5 py-16 text-center">
                    <div className="flex flex-col items-center gap-3">
                      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                        <FolderOpen className="h-6 w-6" />
                      </div>
                      {hasFilters ? (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.users.noMatches')}</p>
                          <Button variant="outline" onClick={clearFilters}>
                            {t('common.clearFilters')}
                          </Button>
                        </>
                      ) : (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.users.noUsers')}</p>
                          <Button onClick={() => setShowCreate(true)}>
                            {t('admin.users.createButton')}
                          </Button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              )}
              {usersQuery.data?.data.map((user) => (
                <tr key={user.id} className="hover:bg-overlay-hover">
                  <td className="px-5 py-3">
                    <Link
                      to="/admin/users/$userId"
                      params={{ userId: user.id }}
                      className="font-medium text-primary hover:underline"
                    >
                      {user.displayName}
                    </Link>
                  </td>
                  <td className="px-5 py-3 text-muted-foreground">{user.email}</td>
                  <td className="px-5 py-3">
                    <StatusBadge isActive={user.isActive} />
                  </td>
                  <td className="px-5 py-3 text-muted-foreground">{user.roleCount}</td>
                  <td className="px-5 py-3 text-muted-foreground">
                    {new Date(user.createdAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {!!usersQuery.data && usersQuery.data.data.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page - 1)}
            disabled={page <= 1 || usersQuery.isFetching}
          >
            {t('admin.users.previousPage')}
          </Button>
          <span>{t('admin.users.pageIndicator', { page, totalPages })}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(page + 1)}
            disabled={page >= totalPages || usersQuery.isFetching}
          >
            {t('admin.users.nextPage')}
          </Button>
        </nav>
      )}
    </div>
  )
}

function StatusBadge({ isActive }: { isActive: boolean }) {
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
      {t(isActive ? 'admin.users.statusActive' : 'admin.users.statusInactive')}
    </span>
  )
}

interface CreateUserFormProps {
  onDone: () => void
}

function CreateUserForm({ onDone }: CreateUserFormProps) {
  const { t } = useTranslation()
  const createMutation = useCreateUserMutation()
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateUserFormValues>({
    resolver: zodResolver(createUserSchema),
    mode: 'onChange',
  })

  const onSubmit = async (values: CreateUserFormValues) => {
    try {
      await createMutation.mutateAsync(values)
      reset()
      onDone()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setError('email', { type: 'server', message: t('admin.users.emailConflict') })
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
      aria-label={t('admin.users.createDialogTitle')}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">
        {t('admin.users.createDialogTitle')}
      </h2>

      <div className="space-y-1.5">
        <Label htmlFor="newuser-email">{t('admin.users.emailLabel')}</Label>
        <Input id="newuser-email" type="email" autoComplete="off" {...register('email')} />
        {errors.email && (
          <p role="alert" className="text-xs text-destructive">
            {errors.email.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newuser-displayname">{t('admin.users.displayNameLabel')}</Label>
        <Input
          id="newuser-displayname"
          type="text"
          autoComplete="off"
          {...register('displayName')}
        />
        {errors.displayName && (
          <p role="alert" className="text-xs text-destructive">
            {errors.displayName.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newuser-password">{t('admin.users.passwordLabel')}</Label>
        <Input
          id="newuser-password"
          type="text"
          autoComplete="off"
          {...register('temporaryPassword')}
        />
        <p className="text-xs text-muted-foreground">{t('admin.users.passwordHelp')}</p>
        {errors.temporaryPassword && (
          <p role="alert" className="text-xs text-destructive">
            {errors.temporaryPassword.message}
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
          {t('admin.users.saveButton')}
        </Button>
        <Button type="button" variant="outline" onClick={onDone}>
          {t('admin.users.cancelButton')}
        </Button>
      </div>
    </form>
  )
}
