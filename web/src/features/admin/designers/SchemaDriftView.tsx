import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { AlertTriangle, CheckCircle2, Database, Loader2, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { ApiError } from '../../../lib/api/apiError'
import { useSchemaDriftQuery } from './useSchemaDriftQuery'
import { useDropColumnMutation } from './schemaDriftMutations'

// Story 5.6 — admin drift view. Lists orphaned columns and lets the operator
// drop them one at a time with an explicit confirmation step (AC-2). The query
// auto-refetches on focus (staleTime: 0); a manual Refresh button also exists.
interface Props {
  designerId: string
}

export function SchemaDriftView({ designerId }: Props) {
  const { t } = useTranslation()
  const query = useSchemaDriftQuery(designerId)
  const dropMutation = useDropColumnMutation(designerId)

  // Tracks which column is awaiting confirmation. null = no dialog open.
  const [confirmingColumn, setConfirmingColumn] = useState<string | null>(null)
  const [dropError, setDropError] = useState<string | null>(null)
  const [dropSuccessMessage, setDropSuccessMessage] = useState<string | null>(null)

  const onConfirmDrop = async (columnName: string) => {
    setDropError(null)
    setDropSuccessMessage(null)
    try {
      await dropMutation.mutateAsync(columnName)
      setDropSuccessMessage(t('admin.designers.drift.dropSuccess'))
    } catch (err) {
      if (err instanceof ApiError && err.code === 'COLUMN_NOT_ORPHANED') {
        setDropError(t('admin.designers.drift.columnNotOrphaned'))
      } else if (err instanceof ApiError && err.code === 'COLUMN_NOT_FOUND') {
        setDropError(t('admin.designers.drift.columnNotFound'))
      } else {
        setDropError(t('admin.designers.drift.dropError'))
      }
    } finally {
      setConfirmingColumn(null)
    }
  }

  if (query.isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} onRefresh={() => {}} refreshDisabled />
        <p className="text-sm text-muted-foreground">{t('admin.designers.drift.loading')}</p>
      </div>
    )
  }
  if (query.isError || !query.data) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} onRefresh={() => { void query.refetch() }} />
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.designers.drift.loadError')}
        </div>
      </div>
    )
  }

  const orphans = query.data.orphanedColumns

  return (
    <div className="space-y-6">
      <PageHeader designerId={designerId} onRefresh={() => { void query.refetch() }} />

      {dropSuccessMessage && (
        <div role="status" className="flex items-start gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{dropSuccessMessage}</span>
        </div>
      )}
      {dropError && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{dropError}</span>
        </div>
      )}

      {orphans.length === 0 ? (
        <div className="rounded-xl border border-border bg-card py-16 shadow-sm">
          <div className="flex flex-col items-center gap-3">
            <div className="flex h-12 w-12 items-center justify-center rounded-full bg-emerald-100 text-emerald-700">
              <CheckCircle2 className="h-6 w-6" />
            </div>
            <p className="font-semibold text-foreground">{t('admin.designers.drift.noOrphans')}</p>
          </div>
        </div>
      ) : (
        <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                  <th className="px-5 py-3">{t('admin.designers.drift.columnNameHeader')}</th>
                  <th className="px-5 py-3">{t('admin.designers.drift.pgTypeHeader')}</th>
                  <th className="px-5 py-3">{t('admin.designers.drift.nonNullCountHeader')}</th>
                  <th className="px-5 py-3 text-right" />
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {orphans.map((col) => (
                  <tr key={col.columnName} className="hover:bg-overlay-hover">
                    <td className="px-5 py-3 font-mono text-xs text-foreground">{col.columnName}</td>
                    <td className="px-5 py-3 font-mono text-xs text-muted-foreground">{col.pgDataType}</td>
                    <td className="px-5 py-3 text-muted-foreground">{col.estimatedNonNullRowCount}</td>
                    <td className="px-5 py-3 text-right">
                      <Button
                        variant="destructive"
                        size="sm"
                        onClick={() => {
                          setDropError(null)
                          setDropSuccessMessage(null)
                          setConfirmingColumn(col.columnName)
                        }}
                        disabled={dropMutation.isPending}
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                        {t('admin.designers.drift.dropButton')}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {confirmingColumn !== null && (
        <div
          role="alertdialog"
          aria-modal="true"
          aria-labelledby="drift-confirm-title"
          aria-describedby="drift-confirm-body"
          className="space-y-3 rounded-xl border border-destructive/30 bg-destructive/10 p-5"
        >
          <div className="flex items-start gap-3">
            <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-destructive/20 text-destructive">
              <AlertTriangle className="h-5 w-5" />
            </div>
            <div className="min-w-0 flex-1">
              <h2 id="drift-confirm-title" className="text-base font-semibold text-destructive">
                {t('admin.designers.drift.confirmTitle')}
              </h2>
              <p id="drift-confirm-body" className="mt-1 text-sm text-destructive">
                {t('admin.designers.drift.confirmBody')}
              </p>
              <p className="mt-2 inline-flex rounded-md bg-background/60 px-2 py-1 font-mono text-xs text-destructive">
                {confirmingColumn}
              </p>
            </div>
          </div>
          <div className="flex gap-2">
            <Button
              type="button"
              variant="destructive"
              onClick={() => {
                void onConfirmDrop(confirmingColumn)
              }}
              disabled={dropMutation.isPending}
            >
              {dropMutation.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  {t('admin.designers.drift.droppingButton')}
                </>
              ) : (
                <>
                  <Trash2 className="h-4 w-4" />
                  {t('admin.designers.drift.confirmButton')}
                </>
              )}
            </Button>
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                setConfirmingColumn(null)
              }}
              disabled={dropMutation.isPending}
            >
              {t('admin.designers.drift.cancelButton')}
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}

interface PageHeaderProps {
  designerId: string
  onRefresh: () => void
  refreshDisabled?: boolean
}

function PageHeader({ designerId, onRefresh, refreshDisabled = false }: PageHeaderProps) {
  const { t } = useTranslation()
  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <div className="flex items-center gap-2.5">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
            <Database className="h-5 w-5" />
          </div>
          <Tooltip>
            <TooltipTrigger asChild>
              <h1 className="truncate text-2xl font-bold tracking-tight">
                {t('admin.designers.drift.title')}
              </h1>
            </TooltipTrigger>
            <TooltipContent>{designerId}</TooltipContent>
          </Tooltip>
        </div>
        <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
          <span className="font-mono">{designerId}</span> · {t('admin.designers.drift.subtitle')}
        </p>
      </div>
      <Button
        variant="outline"
        size="sm"
        onClick={onRefresh}
        disabled={refreshDisabled}
      >
        <RefreshCw className="h-3.5 w-3.5" />
        {t('admin.designers.drift.refreshButton')}
      </Button>
    </div>
  )
}
