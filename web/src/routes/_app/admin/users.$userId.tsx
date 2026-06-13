import { useEffect, useState } from 'react'
import { createFileRoute, Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ArrowLeft } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { useUserDetailQuery } from '../../../features/admin/users/useUserDetailQuery'
import {
  useAssignUserRolesMutation,
  useDeactivateUserMutation,
  useReactivateUserMutation,
  useResetMfaMutation,
  useUpdateUserMutation,
} from '../../../features/admin/users/userMutations'
import { useRolesQuery } from '../../../features/admin/roles/useRolesQuery'
import { ApiError } from '../../../lib/api/apiError'

export const Route = createFileRoute('/_app/admin/users/$userId')({
  component: UserDetailPage,
})

// `updateRequired` is an i18n KEY — the form looks the message up via t(...)
// before rendering, so we never display the raw key string to the user.
// (Story 2.8 review patch P12.)
const UPDATE_REQUIRED_KEY = 'admin.users.updateRequired' as const

const updateUserSchema = z
  .object({
    displayName: z.string().min(1).max(200).optional().or(z.literal('')),
    newPassword: z.string().min(8).optional().or(z.literal('')),
  })
  .refine(
    (v) => (v.displayName ?? '').length > 0 || (v.newPassword ?? '').length > 0,
    { message: UPDATE_REQUIRED_KEY, path: ['displayName'] },
  )
type UpdateUserFormValues = z.infer<typeof updateUserSchema>

function UserDetailPage() {
  const { t } = useTranslation()
  const { userId } = Route.useParams()
  const userQuery = useUserDetailQuery(userId)
  const rolesQuery = useRolesQuery()
  const updateMutation = useUpdateUserMutation(userId)
  const deactivateMutation = useDeactivateUserMutation(userId)
  const reactivateMutation = useReactivateUserMutation(userId)
  const assignRolesMutation = useAssignUserRolesMutation(userId)
  const resetMfaMutation = useResetMfaMutation(userId)

  const [deactivateError, setDeactivateError] = useState<string | null>(null)
  const [resetMfaDialogOpen, setResetMfaDialogOpen] = useState(false)
  const [resetMfaError, setResetMfaError] = useState<string | null>(null)

  if (userQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">{t('admin.users.loading')}</p>
  }
  if (userQuery.isError || !userQuery.data) {
    return (
      <div className="space-y-3">
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.users.userNotFound')}
        </div>
        <Link to="/admin/users" className="inline-flex items-center gap-1 text-sm text-primary hover:underline">
          <ArrowLeft className="h-4 w-4" />
          {t('admin.users.backToUsers')}
        </Link>
      </div>
    )
  }

  const user = userQuery.data

  const onDeactivate = async () => {
    setDeactivateError(null)
    try {
      await deactivateMutation.mutateAsync()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setDeactivateError(t('admin.users.selfDeactivationError'))
        return
      }
      if (err instanceof ApiError && err.status === 404) {
        setDeactivateError(t('admin.users.userNotFoundError'))
        return
      }
      setDeactivateError(t('errors.genericError'))
    }
  }

  const onReactivate = async () => {
    setDeactivateError(null)
    try {
      await reactivateMutation.mutateAsync()
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setDeactivateError(t('admin.users.userNotFoundError'))
        return
      }
      setDeactivateError(t('errors.genericError'))
    }
  }

  const onResetMfa = async () => {
    setResetMfaError(null)
    try {
      await resetMfaMutation.mutateAsync()
      setResetMfaDialogOpen(false)
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setResetMfaError(t('admin.users.userNotFoundError'))
        return
      }
      setResetMfaError(t('admin.users.resetMfaError'))
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <Link
            to="/admin/users"
            className="mb-2 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-3.5 w-3.5" />
            {t('admin.users.backToUsers')}
          </Link>
          <h1 className="text-2xl font-bold tracking-tight">{user.displayName}</h1>
          <p className="text-sm text-muted-foreground">{user.email}</p>
        </div>
        <div className="flex items-center gap-2">
          <StatusBadge isActive={user.isActive} />
          {user.mfaEnabled && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setResetMfaDialogOpen(true)}
            >
              {t('admin.users.resetMfaButton')}
            </Button>
          )}
          {user.isActive ? (
            <Button
              type="button"
              variant="destructive"
              size="sm"
              onClick={() => {
                void onDeactivate()
              }}
              disabled={deactivateMutation.isPending}
            >
              {deactivateMutation.isPending
                ? t('admin.users.loading')
                : t('admin.users.deactivateButton')}
            </Button>
          ) : (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => {
                void onReactivate()
              }}
              disabled={reactivateMutation.isPending}
            >
              {reactivateMutation.isPending
                ? t('admin.users.loading')
                : t('admin.users.reactivateButton')}
            </Button>
          )}
        </div>
      </div>

      <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
        <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1.5 text-sm">
          <dt className="font-medium text-foreground">{t('admin.users.createdAtLabel')}</dt>
          <dd className="text-muted-foreground">{new Date(user.createdAt).toLocaleString()}</dd>
        </dl>
      </div>

      {deactivateError && (
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {deactivateError}
        </div>
      )}

      <UpdateUserForm
        defaultDisplayName={user.displayName}
        onSubmit={async (values) => {
          await updateMutation.mutateAsync({
            displayName: values.displayName?.trim() ? values.displayName.trim() : undefined,
            newPassword: values.newPassword?.trim() ? values.newPassword.trim() : undefined,
          })
        }}
      />

      <RoleAssignment
        key={user.roles.map((r) => r.id).slice().sort().join(',')}
        currentRoleIds={user.roles.map((r) => r.id)}
        availableRoles={rolesQuery.data?.data ?? []}
        isLoading={rolesQuery.isLoading}
        onAssign={(roleIds) => assignRolesMutation.mutateAsync({ roleIds })}
        isPending={assignRolesMutation.isPending}
      />

      <Dialog open={resetMfaDialogOpen} onOpenChange={setResetMfaDialogOpen}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>{t('admin.users.resetMfaDialogTitle')}</DialogTitle>
            <DialogDescription>{t('admin.users.resetMfaDialogBody')}</DialogDescription>
          </DialogHeader>
          {resetMfaError && (
            <p role="alert" className="text-xs text-destructive">{resetMfaError}</p>
          )}
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                setResetMfaDialogOpen(false)
                setResetMfaError(null)
              }}
              disabled={resetMfaMutation.isPending}
            >
              {t('admin.users.resetMfaDialogCancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={() => {
                void onResetMfa()
              }}
              disabled={resetMfaMutation.isPending}
            >
              {resetMfaMutation.isPending
                ? t('admin.users.loading')
                : t('admin.users.resetMfaDialogConfirm')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
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

interface UpdateUserFormProps {
  defaultDisplayName: string
  onSubmit: (values: UpdateUserFormValues) => Promise<void>
}

function UpdateUserForm({ defaultDisplayName, onSubmit }: UpdateUserFormProps) {
  const { t } = useTranslation()
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
    reset,
    setError,
  } = useForm<UpdateUserFormValues>({
    resolver: zodResolver(updateUserSchema),
    mode: 'onChange',
    defaultValues: { displayName: defaultDisplayName, newPassword: '' },
  })

  useEffect(() => {
    reset({ displayName: defaultDisplayName, newPassword: '' })
  }, [defaultDisplayName, reset])

  const submit = async (values: UpdateUserFormValues) => {
    try {
      await onSubmit(values)
      reset({ displayName: values.displayName, newPassword: '' })
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setError('root', { message: t('admin.users.userNotFoundError') })
        return
      }
      setError('root', { message: t('admin.users.saveError') })
    }
  }

  return (
    <form
      onSubmit={(e) => {
        void handleSubmit(submit)(e)
      }}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">{t('admin.users.detailTitle')}</h2>

      <div className="space-y-1.5">
        <Label htmlFor="edit-displayname">{t('admin.users.displayNameLabel')}</Label>
        <Input id="edit-displayname" type="text" {...register('displayName')} />
        {errors.displayName && (
          <p role="alert" className="text-xs text-destructive">
            {/* The Zod refine emits an i18n KEY (UPDATE_REQUIRED_KEY) rather than
                a raw English string — translate it before rendering so the user
                never sees the literal "updateRequired". (Story 2.8 review P12.) */}
            {errors.displayName.message === UPDATE_REQUIRED_KEY
              ? t('admin.users.updateRequired')
              : errors.displayName.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="edit-password">{t('admin.users.newPasswordLabel')}</Label>
        <Input
          id="edit-password"
          type="text"
          autoComplete="off"
          {...register('newPassword')}
        />
        {errors.newPassword && (
          <p role="alert" className="text-xs text-destructive">
            {errors.newPassword.message}
          </p>
        )}
      </div>

      {errors.root && (
        <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {errors.root.message}
        </div>
      )}

      <Button type="submit" disabled={isSubmitting}>
        {t('admin.users.saveButton')}
      </Button>
    </form>
  )
}

interface RoleAssignmentProps {
  currentRoleIds: string[]
  availableRoles: { id: string; name: string }[]
  isLoading: boolean
  onAssign: (roleIds: string[]) => Promise<void>
  isPending: boolean
}

function RoleAssignment({
  currentRoleIds,
  availableRoles,
  isLoading,
  onAssign,
  isPending,
}: RoleAssignmentProps) {
  const { t } = useTranslation()
  const [selected, setSelected] = useState<Set<string>>(() => new Set(currentRoleIds))
  const [assignError, setAssignError] = useState<string | null>(null)

  const toggle = (roleId: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(roleId)) next.delete(roleId)
      else next.add(roleId)
      return next
    })
  }

  const save = async () => {
    setAssignError(null)
    try {
      await onAssign(Array.from(selected))
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setAssignError(t('admin.users.userNotFoundError'))
        return
      }
      setAssignError(t('admin.users.roleAssignError'))
    }
  }

  return (
    <section className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <h2 className="text-lg font-semibold text-foreground">{t('admin.users.rolesHeader')}</h2>
      {isLoading && <p className="text-sm text-muted-foreground">{t('admin.users.loading')}</p>}
      {!isLoading && availableRoles.length === 0 && (
        <p className="text-sm text-muted-foreground">{t('admin.users.rolesEmpty')}</p>
      )}
      <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        {availableRoles.map((role) => (
          <li key={role.id}>
            <label className="flex cursor-pointer items-center gap-2 rounded-md border border-border px-3 py-2 text-sm transition-colors hover:bg-overlay-hover active:bg-overlay-active">
              <input
                type="checkbox"
                checked={selected.has(role.id)}
                onChange={() => toggle(role.id)}
                className="h-4 w-4 rounded border-field-border"
              />
              <span>{role.name}</span>
            </label>
          </li>
        ))}
      </ul>
      {assignError && (
        <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {assignError}
        </div>
      )}
      <Button
        type="button"
        onClick={() => {
          void save()
        }}
        disabled={isPending || isLoading}
      >
        {t('admin.users.saveButton')}
      </Button>
    </section>
  )
}
