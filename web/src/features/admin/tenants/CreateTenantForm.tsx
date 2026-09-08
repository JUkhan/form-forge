import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError } from '@/lib/api/apiError'
import { useCreateTenantMutation } from './tenantMutations'
import type { CreateTenantResponse } from './types'

// Mirrors SafeIdentifier's ^[a-z_][a-z0-9_]{0,62}$ server-side rule (Story 12.2) —
// catching the format client-side avoids a round trip for the common typo case, but
// the server's SafeIdentifier check remains the sole source of truth (Boundaries:
// "do not reimplement it" — this is a UX pre-check, not a re-implementation of the
// reserved-keyword rules, which stay server-only).
const SCHEMA_NAME_REGEX = /^[a-z_][a-z0-9_]{0,62}$/

const createTenantSchema = z.object({
  name: z.string().min(1).max(200),
  schemaName: z
    .string()
    .min(1)
    .max(63)
    .regex(
      SCHEMA_NAME_REGEX,
      'Use lowercase letters, digits, and underscores only (must start with a letter or underscore).',
    ),
})
type CreateTenantFormValues = z.infer<typeof createTenantSchema>

interface CreateTenantFormProps {
  onDone: () => void
  onCreated: (response: CreateTenantResponse) => void
}

export function CreateTenantForm({ onDone, onCreated }: CreateTenantFormProps) {
  const { t } = useTranslation()
  const createMutation = useCreateTenantMutation()
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateTenantFormValues>({
    resolver: zodResolver(createTenantSchema),
    mode: 'onChange',
  })

  const onSubmit = async (values: CreateTenantFormValues) => {
    try {
      const response = await createMutation.mutateAsync(values)
      toast.success(t('admin.tenants.createSuccess', { name: response.tenant.name }))
      reset()
      onCreated(response)
      onDone()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'TENANT_SCHEMA_NAME_CONFLICT') {
        setError('schemaName', { type: 'server', message: t('admin.tenants.schemaNameConflict') })
        return
      }
      if (
        err instanceof ApiError &&
        (err.code === 'TENANT_SCHEMA_NAME_INVALID' || err.code === 'TENANT_SCHEMA_NAME_RESERVED')
      ) {
        setError('schemaName', {
          type: 'server',
          message: err.detail ?? t('admin.tenants.schemaNameInvalid'),
        })
        return
      }
      if (err instanceof ApiError && err.code === 'TENANT_PROVISIONING_FAILED') {
        // The row still exists at status='Error' (Decision) with this schema_name
        // already consumed (uq_tenants_schema_name) — the list, once invalidated by
        // the mutation's onSettled, will show it. Flag schemaName too (not just
        // root) so a retry doesn't resubmit the same now-taken name and land on a
        // confusing TENANT_SCHEMA_NAME_CONFLICT instead of this actionable guidance.
        setError('root', { message: t('admin.tenants.provisioningFailed') })
        setError('schemaName', { type: 'server', message: t('admin.tenants.provisioningFailedSchemaName') })
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
      aria-label={t('admin.tenants.createDialogTitle')}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">{t('admin.tenants.createDialogTitle')}</h2>

      <div className="space-y-1.5">
        <Label htmlFor="newtenant-name">{t('admin.tenants.nameLabel')}</Label>
        <Input id="newtenant-name" type="text" autoComplete="off" {...register('name')} />
        {errors.name && (
          <p role="alert" className="text-xs text-destructive">
            {errors.name.message}
          </p>
        )}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="newtenant-schemaname">{t('admin.tenants.schemaNameLabel')}</Label>
        <Input
          id="newtenant-schemaname"
          type="text"
          autoComplete="off"
          spellCheck={false}
          {...register('schemaName')}
        />
        <p className="text-xs text-muted-foreground">{t('admin.tenants.schemaNameHelp')}</p>
        {errors.schemaName && (
          <p role="alert" className="text-xs text-destructive">
            {errors.schemaName.message}
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
          {isSubmitting ? t('admin.tenants.creatingButton') : t('admin.tenants.saveButton')}
        </Button>
        <Button type="button" variant="outline" onClick={onDone}>
          {t('admin.tenants.cancelButton')}
        </Button>
      </div>
    </form>
  )
}
