import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { History, ScrollText } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { SchemaAuditEntry } from './designerAuditApi'
import { useSchemaAuditLogQuery } from './useSchemaAuditLogQuery'

// Story 5.7 — admin schema audit log view. Paginated table of every DDL emit
// against the provisioned table for one designer, newest first. toVersion = 0
// is a DROP sentinel (not version 0); fromVersion = null is a CREATE.
interface Props {
  designerId: string
}

export function SchemaAuditLogView({ designerId }: Props) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const pageSize = 25
  const query = useSchemaAuditLogQuery(designerId, page, pageSize)

  if (query.isLoading && !query.data) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} />
        <p className="text-sm text-muted-foreground">{t('admin.designers.audit.loading')}</p>
      </div>
    )
  }
  if (query.isError || !query.data) {
    return (
      <div className="space-y-6">
        <PageHeader designerId={designerId} />
        <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t('admin.designers.audit.loadError')}
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
            <p className="font-semibold text-foreground">{t('admin.designers.audit.noEntries')}</p>
          </div>
        </div>
      ) : (
        <>
          <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="bg-muted text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                    <th className="px-4 py-3">{t('admin.designers.audit.columnTimestamp')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnOperation')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnFrom')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnTo')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnColumnsAdded')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnColumnsDropped')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnActor')}</th>
                    <th className="px-4 py-3">{t('admin.designers.audit.columnCorrelationId')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {data.map((entry) => (
                    <AuditRow key={entry.id} entry={entry} t={t} />
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
              {t('admin.designers.audit.prevPage')}
            </Button>
            <span>{t('admin.designers.audit.pageInfo', { page, totalPages })}</span>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages}
            >
              {t('admin.designers.audit.nextPage')}
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
          <History className="h-5 w-5" />
        </div>
        <h1 className="text-2xl font-bold tracking-tight">
          {t('admin.designers.audit.title')}
        </h1>
      </div>
      <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
        <span className="font-mono">{designerId}</span> · {t('admin.designers.audit.subtitle')}
      </p>
    </div>
  )
}

interface RowProps {
  entry: SchemaAuditEntry
  t: ReturnType<typeof useTranslation>['t']
}

function AuditRow({ entry, t }: RowProps) {
  // toVersion = 0 means "DROP — not version-bound" (Story 5.6 sentinel). Render
  // a dash; the Operation column carries the real "DROP" label.
  const toDisplay =
    entry.toVersion === 0
      ? t('admin.designers.audit.versionDropSentinel')
      : String(entry.toVersion)
  const fromDisplay = entry.fromVersion === null ? '—' : String(entry.fromVersion)
  const addedDisplay =
    entry.columnsAdded && entry.columnsAdded.length > 0
      ? entry.columnsAdded.join(', ')
      : t('admin.designers.audit.noColumns')
  const droppedDisplay =
    entry.columnsDropped && entry.columnsDropped.length > 0
      ? entry.columnsDropped.join(', ')
      : t('admin.designers.audit.noColumns')
  const actorDisplay = entry.actorName ?? t('admin.designers.audit.unknownActor')

  return (
    <tr className="hover:bg-overlay-hover">
      <td className="whitespace-nowrap px-4 py-2.5 text-muted-foreground">
        {new Date(entry.timestamp).toLocaleString()}
      </td>
      <td className="px-4 py-2.5">
        <span className="inline-flex items-center rounded-md bg-muted px-1.5 py-0.5 font-mono text-xs text-foreground">
          {entry.ddlOperation}
        </span>
      </td>
      <td className="px-4 py-2.5 text-muted-foreground">{fromDisplay}</td>
      <td className="px-4 py-2.5 text-muted-foreground">{toDisplay}</td>
      <td className="px-4 py-2.5 font-mono text-xs text-muted-foreground">{addedDisplay}</td>
      <td className="px-4 py-2.5 font-mono text-xs text-muted-foreground">{droppedDisplay}</td>
      <td className="px-4 py-2.5 text-muted-foreground">{actorDisplay}</td>
      <td className="px-4 py-2.5 font-mono text-[11px] text-muted-foreground">
        {entry.correlationId ?? '—'}
      </td>
    </tr>
  )
}
