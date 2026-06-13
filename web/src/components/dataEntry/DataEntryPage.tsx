import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from '@tanstack/react-router'
import { toast } from 'sonner'
import { ArrowLeft, Loader2, Save, ShieldAlert } from 'lucide-react'
import DynamicComponent from '@/components/designer/DynamicComponent'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { usePermission } from '@/features/auth/usePermission'
import { usePermissionsQuery } from '@/features/auth/usePermissionsQuery'
import { useCreateRecord } from '@/features/data-entry/useCreateRecord'
import { useDesignerFieldKeys } from '@/features/data-entry/useDesignerFieldKeys'
import type { RecordPayloadWithChildren } from '@/features/data-entry/createRecordApi'
import { ApiError } from '@/lib/api/apiError'

// Story 6.9 — create-record host. The page is mounted at
// `/data/$designerId/new`; the list route renders `RecordListPage` instead.
// The pure-component shape (designerId as a prop) is preserved so unit tests
// can render without a TanStack Router context.
// Story 6.11 — skeleton placeholder for the permissions-loading branch, server
// messageKey-driven error toasts, and inline server validation errors fed back
// into DynamicComponent via the serverErrors prop.
export function DataEntryPage({ designerId }: { designerId: string }) {
  const { t } = useTranslation()
  // `from:` so navigate() is properly typed against the current route's
  // source path. Required for typed navigation to work at runtime against
  // sibling routes within the /data/$designerId/* tree.
  const navigate = useNavigate({ from: '/data/$designerId/new' })
  const { data: permissionsData } = usePermissionsQuery()
  const canCreate = usePermission(designerId, 'canCreate')
  // Shares cache with RecordListPage's schema query; used only to resolve
  // the human-readable schema title for the back link.
  const schema = useDesignerFieldKeys(designerId)
  const headerSchemaTitle = schema.displayName ?? designerId
  const submitRef = useRef<(() => void) | null>(null)
  const [isReady, setIsReady] = useState(false)
  const [isValid, setIsValid] = useState(false)
  const [serverErrors, setServerErrors] = useState<Record<string, string> | undefined>(undefined)
  const mutation = useCreateRecord(designerId)

  useEffect(() => {
    setServerErrors(undefined)
  }, [designerId])

  const handleSave = useCallback(
    (payload: Record<string, unknown>) => {
      setServerErrors(undefined)
      mutation.mutate(payload as RecordPayloadWithChildren, {
        onSuccess: () => {
          toast.success(t('data.entry.createSuccess'))
          void navigate({
            to: '/data/$designerId',
            params: { designerId },
            search: { page: 1, pageSize: 25 },
          })
        },
        onError: (err) => {
          if (err instanceof ApiError && err.fieldErrors) {
            // Flatten the server's per-field message array to the first message,
            // matching the single-string-per-field shape DynamicComponent
            // expects in its errorByBindTo map.
            setServerErrors(
              Object.fromEntries(
                Object.entries(err.fieldErrors).map(([k, msgs]) => [
                  k,
                  msgs[0] ?? t('errors.invalidValue'),
                ]),
              ),
            )
          }
          // AC-3: surface the server's messageKey when present, fall back to
          // the hardcoded generic create-error key otherwise.
          toast.error(
            err instanceof ApiError
              ? t(err.messageKey)
              : t('data.entry.createError'),
          )
        },
      })
    },
    [mutation, t, navigate, designerId],
  )

  // AC-2 — permission gate. We distinguish "permissions still loading" from
  // "genuinely denied" by checking permissionsData directly: usePermission
  // returns false while the query is in-flight, but showing the denied panel
  // immediately would flash it to every authorised user during load.
  if (!permissionsData) {
    return (
      <div className="flex h-full flex-col">
        <DataEntryHeader designerId={designerId} schemaTitle={headerSchemaTitle} actions={null} />
        <div className="flex-1 overflow-y-auto p-6">
          <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
            <div className="space-y-4">
              <Skeleton className="h-6 w-48" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-6 w-48" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-6 w-48" />
              <Skeleton className="h-24 w-full" />
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (!canCreate) {
    return (
      <div className="flex h-full flex-col">
        <DataEntryHeader designerId={designerId} schemaTitle={headerSchemaTitle} actions={null} />
        <div className="flex-1 overflow-y-auto p-6">
          <div className="rounded-xl border border-destructive/30 bg-destructive/10 p-6 shadow-sm">
            <div className="flex items-start gap-3">
              <ShieldAlert className="h-5 w-5 shrink-0 text-destructive" />
              <p className="text-sm text-destructive">
                {t('data.entry.permissionDenied')}
              </p>
            </div>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="flex h-full flex-col">
      <DataEntryHeader
        designerId={designerId}
        schemaTitle={headerSchemaTitle}
        actions={
          <Button
            type="button"
            onClick={() => submitRef.current?.()}
            disabled={!isReady || !isValid || mutation.isPending}
          >
            {mutation.isPending ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" />
                {t('data.entry.saving')}
              </>
            ) : (
              <>
                <Save className="h-4 w-4" />
                {t('data.entry.save')}
              </>
            )}
          </Button>
        }
      />
      <div className="flex-1 overflow-y-auto p-6">
        <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
          <DynamicComponent
            designerId={designerId}
            serverErrors={serverErrors}
            onSave={handleSave}
            onValidityChange={setIsValid}
            onReadyChange={setIsReady}
            submitRef={submitRef}
          />
        </div>
      </div>
    </div>
  )
}

interface DataEntryHeaderProps {
  designerId: string
  schemaTitle: string
  actions: React.ReactNode
}

function DataEntryHeader({ designerId, schemaTitle, actions }: DataEntryHeaderProps) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/data/$designerId/new' })
  return (
    <div className="flex shrink-0 items-center justify-between gap-4 border-b border-border bg-card px-6 py-4">
      <div className="min-w-0">
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              type="button"
              onClick={() => {
                void navigate({
                  to: '/data/$designerId',
                  params: { designerId },
                  search: { page: 1, pageSize: 25 },
                })
              }}
              className="mb-1 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              <span>{schemaTitle}</span>
            </button>
          </TooltipTrigger>
          <TooltipContent>{designerId}</TooltipContent>
        </Tooltip>
        <h1 className="text-lg font-semibold tracking-tight text-foreground">
          {t('data.entry.newRecord')}
        </h1>
      </div>
      {actions}
    </div>
  )
}
