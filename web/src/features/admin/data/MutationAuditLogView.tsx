import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { FileEdit, ScrollText } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { MutationAuditEntry } from './mutationAuditApi'
import { useMutationAuditLogQuery } from './useMutationAuditLogQuery'

// Story 6.8 — admin mutation audit log view. Paginated table of every CRUD
// mutation against the provisioned data table for one designer, newest first.
// Raw JSONB snapshots (previousValues/newValues) are rendered as truncated
// monospace strings — no parsing on the frontend.
interface Props {
  designerId: string
}

export function MutationAuditLogView({ designerId }: Props) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const pageSize = 25
  const query = useMutationAuditLogQuery(designerId, page, pageSize)

  if (query.isLoading && !query.data) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} />
        <p className="text-sm text-muted-foreground">{t('admin.data.audit.loading')}</p>
      </div>
    )
  }
  if (query.isError || !query.data) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} />
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.data.audit.loadError')}
        </div>
      </div>
    )
  }

  const { data, totalPages } = query.data
  const isEmpty = query.data.total === 0

  return (
    <div className={cn('space-y-6', query.isFetching && 'opacity-60')} aria-busy={query.isFetching}>
      <PageHeader designerId={designerId} />

      {isEmpty ? (
        <div className="rounded-xl border border-border bg-card py-16 shadow-sm">
          <div className="flex flex-col items-center gap-3">
            <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
              <ScrollText className="h-6 w-6" />
            </div>
            <p className="font-semibold text-foreground">{t('admin.data.audit.noEntries')}</p>
          </div>
        </div>
      ) : (
        <>
          <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                    <th className="px-4 py-3">{t('admin.data.audit.columnTimestamp')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnOperation')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnRecordId')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnActor')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnPreviousValues')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnNewValues')}</th>
                    <th className="px-4 py-3">{t('admin.data.audit.columnCorrelationId')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {data.map((entry) => (
                    <MutationAuditRow key={entry.id} entry={entry} t={t} />
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <nav
            aria-label="pagination"
            className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
          >
            <Button
              variant="outline"
              size="sm"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page === 1}
            >
              {t('admin.data.audit.prevPage')}
            </Button>
            <span>{t('admin.data.audit.pageInfo', { page, totalPages })}</span>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages}
            >
              {t('admin.data.audit.nextPage')}
            </Button>
          </nav>
        </>
      )}
    </div>
  )
}

function PageHeader({ designerId }: { designerId: string }) {
  const { t } = useTranslation()
  return (
    <div className="min-w-0">
      <div className="flex items-center gap-2.5">
        <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
          <FileEdit className="h-5 w-5" />
        </div>
        <h1 className="text-2xl font-bold tracking-tight">
          {t('admin.data.audit.title')}
        </h1>
      </div>
      <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
        <span className="font-mono">{designerId}</span> · {t('admin.data.audit.subtitle')}
      </p>
    </div>
  )
}

interface RowProps {
  entry: MutationAuditEntry
  t: ReturnType<typeof useTranslation>['t']
}

function MutationAuditRow({ entry, t }: RowProps) {
  const actorDisplay = entry.actorName ?? t('admin.data.audit.unknownActor')
  return (
    <tr className="hover:bg-overlay-hover">
      <td className="whitespace-nowrap px-4 py-2.5 text-muted-foreground">
        {new Date(entry.timestamp).toLocaleString()}
      </td>
      <td className="px-4 py-2.5">
        <span className="inline-flex items-center rounded-md bg-muted px-1.5 py-0.5 font-mono text-xs text-foreground">
          {entry.operation}
        </span>
      </td>
      <td className="px-4 py-2.5 font-mono text-[11px] text-muted-foreground">
        {entry.recordId}
      </td>
      <td className="px-4 py-2.5 text-muted-foreground">{actorDisplay}</td>
      <td className="max-w-[20rem] truncate px-4 py-2.5 font-mono text-[11px] text-muted-foreground">
        {entry.previousValues ?? '—'}
      </td>
      <td className="max-w-[20rem] truncate px-4 py-2.5 font-mono text-[11px] text-muted-foreground">
        {entry.newValues ?? '—'}
      </td>
      <td className="px-4 py-2.5 font-mono text-[11px] text-muted-foreground">
        {entry.correlationId ?? '—'}
      </td>
    </tr>
  )
}
