import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from '@tanstack/react-router'
import { toast } from 'sonner'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Eye, Loader2, Save, Trash2 } from 'lucide-react'
import DynamicComponent from '@/components/designer/DynamicComponent'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { ErrorBanner } from '@/components/shared/ErrorBanner'
import { usePermission } from '@/features/auth/usePermission'
import { useDesignerFieldKeys } from '@/features/data-entry/useDesignerFieldKeys'
import { useRecord } from '@/features/data-entry/useRecord'
import { useUpdateRecord } from '@/features/data-entry/useUpdateRecord'
import { deleteRecord } from '@/features/data-entry/deleteRecordApi'
import type { RecordPagedResult } from '@/features/data-entry/recordListApi'
import type { RecordPayloadWithChildren } from '@/features/data-entry/createRecordApi'
import { ApiError } from '@/lib/api/apiError'

// Story 6.9 — record detail / edit / delete page. Composes:
//   - useRecord            → loads the row (system + user fieldKeys)
//   - useUpdateRecord      → pessimistic PUT mutation (AR-49)
//   - inline optimistic    → DELETE mutation that removes the row from the
//                            list cache BEFORE the server responds (AR-49)
//
// Read-only mode (canUpdate=false) renders DynamicComponent without
// `submitRef`/`onSave` — the form is still mounted with the record's data via
// `initialData` so the user can inspect it, but no Save chrome is rendered so
// the user cannot submit.
//
// Story 6.11 — skeleton form placeholder while the record loads, inline
// ErrorBanner with Retry on blocking fetch failures (the previous
// toast-and-bounce-to-list pattern is gone — the user stays on the route),
// server messageKey-driven toasts on mutation failure, and inline server
// validation errors fed into DynamicComponent via the serverErrors prop.

interface Props {
  designerId: string
  recordId: string
}

export function RecordDetailPage({ designerId, recordId }: Props) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/data/$designerId/$recordId' })
  const queryClient = useQueryClient()

  const canUpdate = usePermission(designerId, 'canUpdate')
  const canDelete = usePermission(designerId, 'canDelete')

  // include=children so a Repeater's existing rows load into the form. Without
  // them, a save would submit an empty children array and prune all child rows.
  const recordQuery = useRecord(designerId, recordId, true)
  // Schema query (shares cache with RecordListPage) — used here only to
  // resolve the human-readable schema title for the back link. Falls back
  // to the raw designerId slug until the schema loads.
  const schema = useDesignerFieldKeys(designerId)
  const headerSchemaTitle = schema.displayName ?? designerId

  const submitRef = useRef<(() => void) | null>(null)
  const [isReady, setIsReady] = useState(false)
  const [isValid, setIsValid] = useState(false)
  const [showDeleteDialog, setShowDeleteDialog] = useState(false)
  const [serverErrors, setServerErrors] = useState<Record<string, string> | undefined>(undefined)

  useEffect(() => {
    setServerErrors(undefined)
  }, [designerId, recordId])

  const updateMutation = useUpdateRecord(designerId)

  // Inline optimistic-delete mutation. Lives at the component scope (NOT in
  // `useDeleteRecord`) because the hook is pessimistic for other consumers
  // (e.g. cascade-delete from the admin schema-drift view). AR-49: snapshot
  // every list-key entry, remove the row, restore on error.
  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteRecord(designerId, id),
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: ['data', designerId] })
      const singleRecordSnapshot = queryClient.getQueryData([
        'data', designerId, 'record', recordId,
      ])
      const snapshot = queryClient.getQueriesData<RecordPagedResult>({
        queryKey: ['data', designerId, 'list'],
      })
      queryClient.setQueriesData<RecordPagedResult>(
        { queryKey: ['data', designerId, 'list'] },
        (old) => {
          if (!old) return old
          const newTotal = Math.max(0, old.total - 1)
          return {
            ...old,
            data: old.data.filter((r) => r.id !== id),
            total: newTotal,
            totalPages: Math.max(1, Math.ceil(newTotal / old.pageSize)),
          }
        },
      )
      return { snapshot, singleRecordSnapshot }
    },
    onError: (err, _id, context) => {
      if (context?.snapshot) {
        for (const [key, value] of context.snapshot) {
          queryClient.setQueryData(key, value)
        }
      }
      if (context?.singleRecordSnapshot !== undefined) {
        queryClient.setQueryData(
          ['data', designerId, 'record', recordId],
          context.singleRecordSnapshot,
        )
      }
      toast.error(
        err instanceof ApiError ? t(err.messageKey) : t('data.entry.deleteError'),
      )
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      toast.success(t('data.entry.deleteSuccess'))
      setShowDeleteDialog(false)
      void navigate({
        to: '/data/$designerId',
        params: { designerId },
        search: { page: 1, pageSize: 25 },
      })
    },
  })

  const handleSave = useCallback(
    (payload: Record<string, unknown>) => {
      setServerErrors(undefined)
      updateMutation.mutate(
        { id: recordId, payload: payload as RecordPayloadWithChildren },
        {
          onSuccess: () => toast.success(t('data.entry.updateSuccess')),
          onError: (err) => {
            if (err instanceof ApiError && err.fieldErrors) {
              setServerErrors(
                Object.fromEntries(
                  Object.entries(err.fieldErrors).map(([k, msgs]) => [
                    k,
                    msgs[0] ?? t('errors.invalidValue'),
                  ]),
                ),
              )
            }
            toast.error(
              err instanceof ApiError ? t(err.messageKey) : t('data.entry.updateError'),
            )
          },
        },
      )
    },
    [updateMutation, recordId, t],
  )

  if (recordQuery.isLoading && !recordQuery.data) {
    return (
      <div className="flex h-full flex-col">
        <RecordHeader designerId={designerId} schemaTitle={headerSchemaTitle} canUpdate={false} actions={null} />
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

  if (recordQuery.isError && !recordQuery.data) {
    return (
      <div className="flex h-full flex-col">
        <RecordHeader designerId={designerId} schemaTitle={headerSchemaTitle} canUpdate={false} actions={null} />
        <div className="flex-1 overflow-y-auto p-6">
          <ErrorBanner
            error={recordQuery.error}
            onRetry={() => void recordQuery.refetch()}
          />
        </div>
      </div>
    )
  }

  const record = recordQuery.data
  const initialData = record as Record<string, unknown> | undefined

  const actions = (
    <div className="flex items-center gap-2">
      {canDelete && (
        <Button
          type="button"
          variant="destructive"
          onClick={() => setShowDeleteDialog(true)}
          disabled={deleteMutation.isPending}
        >
          <Trash2 className="h-4 w-4" />
          {t('data.entry.delete')}
        </Button>
      )}
      {canUpdate && (
        <Button
          type="button"
          onClick={() => submitRef.current?.()}
          disabled={!isReady || !isValid || updateMutation.isPending}
        >
          {updateMutation.isPending ? (
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
      )}
    </div>
  )

  return (
    <div className="flex h-full flex-col">
      <RecordHeader
        designerId={designerId}
        schemaTitle={headerSchemaTitle}
        canUpdate={canUpdate}
        actions={actions}
      />
      <div className="flex-1 overflow-y-auto p-6">
        <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
          {canUpdate ? (
            <DynamicComponent
              designerId={designerId}
              initialData={initialData}
              recordId={recordId}
              serverErrors={serverErrors}
              onSave={handleSave}
              onValidityChange={setIsValid}
              onReadyChange={setIsReady}
              submitRef={submitRef}
            />
          ) : (
            // Read-only mode: no submitRef/onSave wired. DynamicComponent will
            // render with `initialData` populated but no submission path exists.
            <DynamicComponent designerId={designerId} initialData={initialData} />
          )}
        </div>
      </div>

      <Dialog open={showDeleteDialog} onOpenChange={setShowDeleteDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('data.entry.deleteConfirmTitle')}</DialogTitle>
            <DialogDescription>
              {t('data.entry.deleteConfirmDescription')}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => setShowDeleteDialog(false)}
              disabled={deleteMutation.isPending}
            >
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={() => deleteMutation.mutate(recordId)}
              disabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  {t('data.entry.delete')}
                </>
              ) : (
                <>
                  <Trash2 className="h-4 w-4" />
                  {t('data.entry.delete')}
                </>
              )}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

interface RecordHeaderProps {
  designerId: string
  schemaTitle: string
  canUpdate: boolean
  actions: React.ReactNode
}

function RecordHeader({ designerId, schemaTitle, canUpdate, actions }: RecordHeaderProps) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/data/$designerId/$recordId' })
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
        <div className="flex items-center gap-2">
          {!canUpdate && (
            <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">
              <Eye className="h-3 w-3" />
              {t('data.entry.readOnly')}
            </span>
          )}
        </div>
      </div>
      {actions}
    </div>
  )
}
