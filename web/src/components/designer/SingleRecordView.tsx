import { useCallback, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Loader2, Save } from 'lucide-react'
import DynamicComponent from './DynamicComponent'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorBanner } from '@/components/shared/ErrorBanner'
import type { DesignerElement } from '@/types/designer'
import {
  createSingleRecord,
  getSingleRecord,
  updateSingleRecord,
} from '@/features/data-entry/singleRecordApi'
import type { RecordPayloadWithChildren } from '@/features/data-entry/createRecordApi'
import { ApiError } from '@/lib/api/apiError'

// Runtime renderer for the SingleRecord component. It renders ANOTHER (CRUD) designer's
// form via DynamicComponent and reads/writes the ONE record that belongs to the current
// user, scoped server-side by the `authFilterColumn` property. When the user has no record
// yet, the form starts empty (create mode); after the first save it switches to update mode.
export default function SingleRecordView({ element }: { element: DesignerElement }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()

  const p = element.properties
  const designerId = typeof p.optionsDesignerId === 'string' ? p.optionsDesignerId.trim() : ''
  const version = typeof p.optionsVersion === 'number' ? p.optionsVersion : undefined
  const authFilterColumn = typeof p.authFilterColumn === 'string' ? p.authFilterColumn.trim() : ''
  const configured = designerId !== '' && authFilterColumn !== ''

  const submitRef = useRef<(() => void) | null>(null)
  const [isReady, setIsReady] = useState(false)
  const [isValid, setIsValid] = useState(false)
  const [serverErrors, setServerErrors] = useState<Record<string, string> | undefined>(undefined)

  const queryKey = ['single-record', designerId, version ?? 'latest', authFilterColumn] as const
  const recordQuery = useQuery({
    queryKey,
    // include children so a form with Repeaters loads its existing rows.
    queryFn: () => getSingleRecord(designerId, authFilterColumn, true),
    enabled: configured,
    staleTime: 0,
  })

  const record = recordQuery.data
  const recordId = record && typeof record.id === 'string' ? record.id : undefined
  const initialData = record as Record<string, unknown> | undefined

  const mutation = useMutation({
    mutationFn: (payload: RecordPayloadWithChildren) =>
      recordId
        ? updateSingleRecord(designerId, recordId, authFilterColumn, payload)
        : createSingleRecord(designerId, authFilterColumn, payload),
  })

  const handleSave = useCallback(
    (payload: Record<string, unknown>) => {
      setServerErrors(undefined)
      mutation.mutate(payload as RecordPayloadWithChildren, {
        onSuccess: () => {
          toast.success(t(recordId ? 'data.entry.updateSuccess' : 'data.entry.createSuccess'))
          // Refetch so a fresh create flips the form into update mode (recordId now set).
          // Key built inline (same inputs as the query) to keep this callback's deps stable.
          void queryClient.invalidateQueries({
            queryKey: ['single-record', designerId, version ?? 'latest', authFilterColumn],
          })
        },
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
            err instanceof ApiError
              ? t(err.messageKey)
              : t(recordId ? 'data.entry.updateError' : 'data.entry.createError'),
          )
        },
      })
    },
    // queryKey is derived from these same inputs, so listing them keeps the closure fresh.
    [mutation, t, recordId, queryClient, designerId, version, authFilterColumn],
  )

  if (!configured) {
    return (
      <div className="rounded-lg border border-dashed border-border bg-muted px-4 py-6 text-center text-sm text-muted-foreground">
        {t('designer.singleRecord.notConfigured')}
      </div>
    )
  }

  if (recordQuery.isLoading) {
    return (
      <div className="space-y-4 rounded-xl border border-border bg-card p-4">
        <Skeleton className="h-6 w-48" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
      </div>
    )
  }

  if (recordQuery.isError) {
    return <ErrorBanner error={recordQuery.error} onRetry={() => void recordQuery.refetch()} />
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4">
      <DynamicComponent
        // Remount when switching create→update so initialData re-seeds cleanly.
        key={recordId ?? 'new'}
        designerId={designerId}
        version={version}
        initialData={initialData}
        recordId={recordId}
        hiddenFieldKey={authFilterColumn}
        serverErrors={serverErrors}
        onSave={handleSave}
        onValidityChange={setIsValid}
        onReadyChange={setIsReady}
        submitRef={submitRef}
      />
      <div className="flex justify-end">
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
      </div>
    </div>
  )
}
