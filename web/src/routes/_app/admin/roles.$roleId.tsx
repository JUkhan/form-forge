import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react'
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ArrowLeft, Info } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useRoleDetailQuery } from '../../../features/admin/roles/useRoleDetailQuery'
import {
  useDeleteRoleMutation,
  useUpdateRoleMutation,
} from '../../../features/admin/roles/roleMutations'
import type { PermissionRecord, RoleDetail } from '../../../features/admin/roles/types'
import { designerApi } from '../../../features/designer/designerApi'
import { ApiError } from '../../../lib/api/apiError'

// Cap on designers fetched to populate the matrix. Same logic as the menu
// bind picker — installations with more than this need a search UI.
const DESIGNER_LOOKUP_PAGE_SIZE = 100

export const Route = createFileRoute('/_app/admin/roles/$roleId')({
  component: RoleDetailPage,
})

// Mirror the server-side FluentValidation regex on UpdateRoleRequestValidator.
const ROLE_NAME_REGEX = /^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$/

const updateRoleSchema = z.object({
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
type UpdateRoleFormValues = z.infer<typeof updateRoleSchema>

type PermissionFlag = 'canCreate' | 'canRead' | 'canUpdate' | 'canDelete' | 'canExport'
const PERMISSION_FLAGS: readonly PermissionFlag[] = [
  'canCreate',
  'canRead',
  'canUpdate',
  'canDelete',
  'canExport',
] as const

function sortByResourceId(permissions: PermissionRecord[]): PermissionRecord[] {
  return [...permissions].sort((a, b) => a.resourceId.localeCompare(b.resourceId))
}

function RoleDetailPage() {
  const { t } = useTranslation()
  const { roleId } = Route.useParams()
  const roleQuery = useRoleDetailQuery(roleId)

  if (roleQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">{t('admin.roles.loading')}</p>
  }
  if (roleQuery.isError || !roleQuery.data) {
    return (
      <div className="space-y-3">
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.roles.roleNotFound')}
        </div>
        <Link to="/admin/roles" className="inline-flex items-center gap-1 text-sm text-primary hover:underline">
          <ArrowLeft className="h-4 w-4" />
          {t('admin.roles.backToRoles')}
        </Link>
      </div>
    )
  }

  return <RoleDetailContent role={roleQuery.data} roleId={roleId} />
}

interface RoleDetailContentProps {
  role: RoleDetail
  roleId: string
}

interface PermissionMatrixHandle {
  getDraft: () => PermissionRecord[]
}

function RoleDetailContent({ role, roleId }: RoleDetailContentProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const updateMutation = useUpdateRoleMutation(roleId)
  const deleteMutation = useDeleteRoleMutation(roleId)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  // Story 8.2 — dataset-management capability toggle. Lives outside PermissionMatrix
  // (which owns per-resource CRUD flags) as plain state, included in the save payload.
  const [canManageDatasets, setCanManageDatasets] = useState(role.canManageDatasets)

  // P6 fix: without this query, the matrix only shows resources the role
  // ALREADY has permission rows for — a brand-new role has none and the
  // empty-state lands with no entry point to add resources. Pre-populate the
  // matrix with every designer so admins can grant CRUD by checkbox.
  const designersQuery = useQuery({
    queryKey: ['designer', 'list', 1, DESIGNER_LOOKUP_PAGE_SIZE],
    queryFn: () => designerApi.listSchemas(1, DESIGNER_LOOKUP_PAGE_SIZE),
    staleTime: 30_000,
  })

  // Merge existing permission rows with a synthetic default-false row for any
  // designer the role hasn't yet been granted permission on. Order: existing
  // rows first (preserved by sort), then alphabetically. Resource ids from
  // existing rows that don't map to any designer (e.g. 'platform-admin')
  // remain so they aren't silently dropped by the next save.
  const mergedPermissions = useMemo<PermissionRecord[]>(() => {
    const existing = role.permissions
    const existingIds = new Set(existing.map((p) => p.resourceId))
    const designers = designersQuery.data?.data ?? []
    const synthetic: PermissionRecord[] = designers
      .filter((d) => !existingIds.has(d.designerId))
      .map((d) => ({
        resourceId: d.designerId,
        canCreate: false,
        canRead: false,
        canUpdate: false,
        canDelete: false,
        canExport: false,
      }))
    return [...existing, ...synthetic].sort((a, b) =>
      a.resourceId.localeCompare(b.resourceId),
    )
  }, [role.permissions, designersQuery.data])

  // A single Save commits name + description + permissions atomically. The
  // matrix manages its own local draft state (keyed remount snaps it to
  // server truth whenever the resource set changes — AC-3) and exposes the
  // current draft via an imperative handle so this parent submit handler
  // can read it at click time.
  const matrixRef = useRef<PermissionMatrixHandle>(null)

  const {
    register,
    handleSubmit,
    formState: { errors, isDirty },
    reset,
    setError,
  } = useForm<UpdateRoleFormValues>({
    resolver: zodResolver(updateRoleSchema),
    mode: 'onChange',
    defaultValues: { name: role.name, description: role.description ?? '' },
  })

  // Re-sync the form when the role record changes underneath us (e.g. window
  // focus refetch), but only if the admin has no unsaved edits — otherwise a
  // background refetch would wipe their in-progress typing.
  useEffect(() => {
    if (isDirty) return
    reset({ name: role.name, description: role.description ?? '' })
  }, [role.name, role.description, reset, isDirty])

  // Re-sync the dataset-management toggle with server truth on background refetch,
  // gated on the name/description dirty flag (same heuristic as the form reset above)
  // so an in-progress edit isn't clobbered. Uses React's "adjust state during render"
  // pattern (tracking the last-synced server value) rather than an effect — this avoids
  // a cascading re-render and the react-hooks/set-state-in-effect lint rule.
  const [lastSyncedDatasets, setLastSyncedDatasets] = useState(role.canManageDatasets)
  const checkboxDirty = canManageDatasets !== lastSyncedDatasets
  if (!isDirty && !checkboxDirty && role.canManageDatasets !== lastSyncedDatasets) {
    setLastSyncedDatasets(role.canManageDatasets)
    setCanManageDatasets(role.canManageDatasets)
  }

  const submit = async (values: UpdateRoleFormValues) => {
    const trimmedDescription = values.description?.trim() ? values.description.trim() : null
    const draft = matrixRef.current?.getDraft() ?? role.permissions
    try {
      await updateMutation.mutateAsync({
        name: values.name,
        description: trimmedDescription,
        permissions: draft,
        canManageDatasets,
      })
      reset({ name: values.name, description: trimmedDescription ?? '' })
      setLastSyncedDatasets(canManageDatasets)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'ROLE_NAME_CONFLICT') {
        setError('name', { type: 'server', message: t('admin.roles.nameConflict') })
        return
      }
      if (err instanceof ApiError && err.status === 409 && err.code === 'ROLE_SYSTEM_PROTECTED') {
        setError('root', { message: t('admin.roles.systemProtected') })
        return
      }
      if (err instanceof ApiError && err.status === 404) {
        setError('root', { message: t('admin.roles.roleNotFoundError') })
        return
      }
      setError('root', { message: t('admin.roles.saveError') })
    }
  }

  const onDelete = async () => {
    if (role.isSystem) return
    setDeleteError(null)
    try {
      await deleteMutation.mutateAsync()
      void navigate({ to: '/admin/roles' })
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'ROLE_HAS_ASSIGNMENTS') {
        setDeleteError(t('admin.roles.hasAssignments'))
        return
      }
      if (err instanceof ApiError && err.status === 409 && err.code === 'ROLE_SYSTEM_PROTECTED') {
        setDeleteError(t('admin.roles.systemProtected'))
        return
      }
      if (err instanceof ApiError && err.status === 404) {
        setDeleteError(t('admin.roles.roleNotFoundError'))
        return
      }
      setDeleteError(t('admin.roles.deleteError'))
    }
  }

  const isSaving = updateMutation.isPending
  const isDeleting = deleteMutation.isPending

  return (
    <div className="space-y-6">
      <div className="min-w-0">
        <Link
          to="/admin/roles"
          className="mb-2 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3.5 w-3.5" />
          {t('admin.roles.backToRoles')}
        </Link>
        <h1 className="text-2xl font-bold tracking-tight">{role.name}</h1>
        {role.description && <p className="text-sm text-muted-foreground">{role.description}</p>}
      </div>

      {role.isSystem && (
        <div
          role="status"
          className="flex items-start gap-2 rounded-lg border border-indigo-200 bg-indigo-50 px-4 py-3 text-sm text-indigo-900"
        >
          <Info className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{t('admin.roles.systemRoleNotice')}</span>
        </div>
      )}

      <form
        onSubmit={(e) => {
          void handleSubmit(submit)(e)
        }}
        className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
      >
        <h2 className="text-lg font-semibold text-foreground">{t('admin.roles.detailTitle')}</h2>

        <div className="space-y-1.5">
          <Label htmlFor="edit-role-name">{t('admin.roles.nameLabel')}</Label>
          <Input id="edit-role-name" type="text" disabled={role.isSystem} {...register('name')} />
          {errors.name && (
            <p role="alert" className="text-xs text-destructive">
              {errors.name.message}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="edit-role-description">{t('admin.roles.descriptionLabel')}</Label>
          <Input
            id="edit-role-description"
            type="text"
            disabled={role.isSystem}
            {...register('description')}
          />
          {errors.description && (
            <p role="alert" className="text-xs text-destructive">
              {errors.description.message}
            </p>
          )}
        </div>

        {/* Story 8.2 — dataset-management capability, above the per-resource matrix.
            Writable for custom roles; read-only for system roles. */}
        <div className="flex items-center justify-between rounded-lg border border-border bg-muted/40 p-4">
          <div>
            <p className="text-sm font-medium text-foreground">
              {t('admin.roles.datasetManagementLabel')}
            </p>
            <p className="mt-0.5 text-xs text-muted-foreground">
              {t('admin.roles.datasetManagementHelp')}
            </p>
          </div>
          <input
            type="checkbox"
            aria-label={t('admin.roles.datasetManagementLabel')}
            checked={canManageDatasets}
            disabled={role.isSystem}
            onChange={(e) => setCanManageDatasets(e.target.checked)}
            className="h-4 w-4 rounded border-field-border"
          />
        </div>

        {/* Key includes the merged resource set so the matrix remounts (and
            its local draft re-seeds from server truth) when the designer list
            arrives or any resource is added/removed by another tab. */}
        <PermissionMatrix
          key={`${roleId}:${mergedPermissions
            .map((p) => p.resourceId)
            .join(',')}`}
          ref={matrixRef}
          initialPermissions={mergedPermissions}
          readOnly={role.isSystem}
          isLoading={designersQuery.isLoading}
        />

        {errors.root && (
          <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
            {errors.root.message}
          </div>
        )}

        {!role.isSystem && (
          <Button type="submit" disabled={isSaving}>
            {isSaving ? t('admin.roles.savingButton') : t('admin.roles.saveButton')}
          </Button>
        )}
      </form>

      {!role.isSystem && (
        <div className="space-y-2">
          <Button
            type="button"
            variant="destructive"
            onClick={() => {
              void onDelete()
            }}
            disabled={isDeleting}
          >
            {isDeleting ? t('admin.roles.deletingButton') : t('admin.roles.deleteButton')}
          </Button>
          {deleteError && (
            <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {deleteError}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

interface PermissionMatrixProps {
  initialPermissions: PermissionRecord[]
  readOnly: boolean
  isLoading?: boolean
}

const PermissionMatrix = forwardRef<PermissionMatrixHandle, PermissionMatrixProps>(
  function PermissionMatrix({ initialPermissions, readOnly, isLoading = false }, ref) {
    const { t } = useTranslation()
    const [draft, setDraft] = useState<PermissionRecord[]>(() =>
      sortByResourceId(initialPermissions),
    )

    useImperativeHandle(ref, () => ({ getDraft: () => draft }), [draft])

    const toggleFlag = (resourceId: string, flag: PermissionFlag) => {
      setDraft((prev) =>
        prev.map((row) => (row.resourceId === resourceId ? { ...row, [flag]: !row[flag] } : row)),
      )
    }

    return (
      <section className="space-y-3 rounded-lg border border-border bg-muted/40 p-4">
        <div>
          <h3 className="text-base font-semibold text-foreground">{t('admin.roles.matrixTitle')}</h3>
          <p className="mt-1 text-xs text-muted-foreground">{t('admin.roles.matrixHelp')}</p>
        </div>

        {isLoading ? (
          <p className="text-sm text-muted-foreground">{t('admin.roles.matrixLoading')}</p>
        ) : draft.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t('admin.roles.matrixEmptyState')}</p>
        ) : (
          <div className="overflow-hidden rounded-lg border border-border bg-card">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                    <th className="px-4 py-2.5">{t('admin.roles.resourceLabel')}</th>
                    <th className="px-4 py-2.5">{t('admin.roles.canCreate')}</th>
                    <th className="px-4 py-2.5">{t('admin.roles.canRead')}</th>
                    <th className="px-4 py-2.5">{t('admin.roles.canUpdate')}</th>
                    <th className="px-4 py-2.5">{t('admin.roles.canDelete')}</th>
                    <th className="px-4 py-2.5">{t('admin.roles.canExport')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {draft.map((row) => (
                    <tr key={row.resourceId} className="hover:bg-overlay-hover">
                      <td className="px-4 py-2.5 font-mono text-xs text-foreground">
                        {row.resourceId}
                      </td>
                      {PERMISSION_FLAGS.map((flag) => (
                        <td key={flag} className="px-4 py-2.5">
                          <input
                            type="checkbox"
                            aria-label={`${row.resourceId} ${t(`admin.roles.${flag}`)}`}
                            checked={row[flag]}
                            disabled={readOnly}
                            onChange={() => toggleFlag(row.resourceId, flag)}
                            className="h-4 w-4 rounded border-field-border"
                          />
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </section>
    )
  },
)
