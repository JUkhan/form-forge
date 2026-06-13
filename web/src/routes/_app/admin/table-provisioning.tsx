import { useCallback, useState } from 'react'
import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { z } from 'zod'
import { Database, Table2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { SearchBox } from '@/components/shared/SearchBox'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { ApiError } from '../../../lib/api/apiError'
import type { TableProvisioningItem } from '../../../features/admin/tableProvisioning/tableProvisioningApi'
import {
  useProvisionTableMutation,
  useTableProvisioningQuery,
} from '../../../features/admin/tableProvisioning/useTableProvisioningQueries'

const searchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  // Free-text filter on display name OR designer id.
  q: z.string().optional(),
})

export const Route = createFileRoute('/_app/admin/table-provisioning')({
  validateSearch: searchSchema,
  component: TableProvisioningPage,
})

function TableProvisioningPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/table-provisioning' })
  const { page, pageSize, q } = Route.useSearch()
  const query = useTableProvisioningQuery({ page, pageSize, search: q })

  // navigate() is stable; useCallback keeps these handlers referentially stable so
  // SearchBox's debounce timer isn't reset on every parent render. A new search
  // resets to page 1 so results aren't hidden on an out-of-range page.
  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )

  const items = query.data?.data ?? []
  const total = query.data?.total ?? 0
  const totalPages = Math.max(1, query.data?.totalPages ?? 1)
  const clampedPage = Math.min(page, totalPages)
  const hasResults = items.length > 0
  const emptyFiltered = !!query.data && !hasResults && !!q
  const emptyAll = !!query.data && !hasResults && !q

  return (
    <div className="space-y-6">
      <div className="min-w-0">
        <div className="flex items-center gap-2.5">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
            <Database className="h-5 w-5" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight">
            {t('admin.tableProvisioning.title')}
          </h1>
        </div>
        <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
          {t('admin.tableProvisioning.subtitle')}
        </p>
      </div>

      <SearchBox
        value={q ?? ''}
        onChange={setSearch}
        placeholder={t('admin.tableProvisioning.searchPlaceholder')}
      />

      {query.isError && (
        <div
          role="alert"
          className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          {t('admin.tableProvisioning.loadError')}
        </div>
      )}

      {query.isLoading && (
        <p className="text-sm text-muted-foreground">{t('admin.tableProvisioning.loading')}</p>
      )}

      {emptyAll && (
        <div className="rounded-xl border border-border bg-card p-10 text-center">
          <Table2 className="mx-auto h-6 w-6 text-muted-foreground/60" />
          <p className="mt-2 text-sm text-muted-foreground">
            {t('admin.tableProvisioning.noDesigners')}
          </p>
        </div>
      )}

      {emptyFiltered && (
        <div className="rounded-xl border border-border bg-card p-10 text-center">
          <Table2 className="mx-auto h-6 w-6 text-muted-foreground/60" />
          <p className="mt-2 text-sm text-muted-foreground">
            {t('admin.tableProvisioning.noMatches')}
          </p>
        </div>
      )}

      {hasResults && (
        <>
          <div className="overflow-hidden rounded-xl border border-border">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="bg-muted text-left">
                    <th className="px-4 py-2.5 font-medium">
                      {t('admin.tableProvisioning.columnComponent')}
                    </th>
                    <th className="px-4 py-2.5 font-medium">
                      {t('admin.tableProvisioning.columnTable')}
                    </th>
                    <th className="px-4 py-2.5 font-medium">
                      {t('admin.tableProvisioning.columnStatus')}
                    </th>
                    <th className="px-4 py-2.5 font-medium">
                      {t('admin.tableProvisioning.columnLastProvisioned')}
                    </th>
                    <th className="px-4 py-2.5 text-right font-medium">
                      {t('admin.tableProvisioning.columnActions')}
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {items.map((item) => (
                    <DesignerRow key={item.designerId} item={item} />
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className="flex items-center justify-between text-sm text-muted-foreground">
            <span>{t('admin.tableProvisioning.totalCount', { count: total })}</span>
            <div className="flex items-center gap-3">
              <Button
                variant="outline"
                size="sm"
                onClick={() => goToPage(clampedPage - 1)}
                disabled={clampedPage <= 1 || query.isFetching}
              >
                {t('admin.tableProvisioning.previousPage')}
              </Button>
              <span>
                {t('admin.tableProvisioning.pageIndicator', { page: clampedPage, totalPages })}
              </span>
              <Button
                variant="outline"
                size="sm"
                onClick={() => goToPage(clampedPage + 1)}
                disabled={clampedPage >= totalPages || query.isFetching}
              >
                {t('admin.tableProvisioning.nextPage')}
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  )
}

function StatusBadge({ provisioned }: { provisioned: boolean }) {
  const { t } = useTranslation()
  const spec = provisioned
    ? { bg: '#d4f4dd', fg: '#1b6b3a', key: 'admin.tableProvisioning.statusProvisioned' }
    : { bg: 'hsl(var(--muted))', fg: 'hsl(var(--muted-foreground))', key: 'admin.tableProvisioning.statusNotProvisioned' }
  return (
    <span
      style={{
        backgroundColor: spec.bg,
        color: spec.fg,
        padding: '0.15rem 0.5rem',
        borderRadius: '999px',
        fontSize: '0.75rem',
        fontWeight: 600,
      }}
    >
      {t(spec.key)}
    </span>
  )
}

function DesignerRow({ item }: { item: TableProvisioningItem }) {
  const { t } = useTranslation()
  const mutation = useProvisionTableMutation()

  const hasPublished = item.publishedVersions.length > 0
  // Default to the latest Published version (publishedVersions is descending).
  const [version, setVersion] = useState<string>(
    hasPublished ? String(item.publishedVersions[0]) : '',
  )
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = () => {
    setError(null)
    void mutation
      .mutateAsync({ designerId: item.designerId, version: Number(version) })
      .then(() => setConfirmOpen(false))
      .catch((err) => {
        const msg =
          err instanceof ApiError && err.messageKey ? t(err.messageKey) : t('errors.genericError')
        setError(msg)
      })
  }

  const onClickAction = () => {
    setError(null)
    // Re-syncing an existing table runs ALTER on live data — confirm first. A
    // first-time provision is a plain CREATE, so run it directly.
    if (item.isProvisioned) setConfirmOpen(true)
    else run()
  }

  const lastProvisioned =
    item.lastProvisionedAt != null
      ? t('admin.tableProvisioning.lastProvisionedValue', {
          version: item.lastProvisionedVersion ?? '?',
          date: new Date(item.lastProvisionedAt).toLocaleString(),
        })
      : '—'

  return (
    <tr className="hover:bg-overlay-hover">
      <td className="px-4 py-3 align-top">
        <div className="font-medium text-foreground">{item.displayName}</div>
        <div className="font-mono text-xs text-muted-foreground">{item.designerId}</div>
      </td>
      <td className="px-4 py-3 align-top">
        <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs text-foreground">
          {item.tableName}
        </code>
      </td>
      <td className="px-4 py-3 align-top">
        <StatusBadge provisioned={item.isProvisioned} />
      </td>
      <td className="px-4 py-3 align-top text-xs text-muted-foreground">{lastProvisioned}</td>
      <td className="px-4 py-3 align-top">
        {hasPublished ? (
          <div className="flex items-center justify-end gap-2">
            <div className="flex items-center gap-1.5">
              <Label htmlFor={`ver-${item.designerId}`} className="sr-only">
                {t('admin.tableProvisioning.versionLabel')}
              </Label>
              <Select value={version} onValueChange={setVersion}>
                <SelectTrigger id={`ver-${item.designerId}`} size="sm" className="w-28">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {item.publishedVersions.map((v) => (
                    <SelectItem key={v} value={String(v)}>
                      {t('admin.tableProvisioning.versionOption', { version: v })}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <Button size="sm" onClick={onClickAction} disabled={mutation.isPending || !version}>
              {item.isProvisioned
                ? t('admin.tableProvisioning.resyncButton')
                : t('admin.tableProvisioning.provisionButton')}
            </Button>
          </div>
        ) : (
          <p className="text-right text-xs text-muted-foreground">
            {t('admin.tableProvisioning.noPublishedVersion')}
          </p>
        )}
        {error && (
          <p role="alert" className="mt-2 text-right text-xs text-destructive">
            {error}
          </p>
        )}

        <AlertDialog
          open={confirmOpen}
          onOpenChange={(open) => {
            if (!open) {
              setConfirmOpen(false)
              setError(null)
            }
          }}
        >
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>
                {t('admin.tableProvisioning.resyncConfirmTitle')}
              </AlertDialogTitle>
              <AlertDialogDescription>
                {t('admin.tableProvisioning.resyncConfirmDescription', {
                  table: item.tableName,
                  version,
                })}
              </AlertDialogDescription>
            </AlertDialogHeader>
            {error && (
              <div
                role="alert"
                className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
              >
                {error}
              </div>
            )}
            <AlertDialogFooter>
              <AlertDialogCancel disabled={mutation.isPending}>
                {t('admin.tableProvisioning.cancelButton')}
              </AlertDialogCancel>
              <AlertDialogAction
                disabled={mutation.isPending}
                onClick={(e) => {
                  e.preventDefault()
                  run()
                }}
              >
                {t('admin.tableProvisioning.resyncConfirmButton')}
              </AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </td>
    </tr>
  )
}
