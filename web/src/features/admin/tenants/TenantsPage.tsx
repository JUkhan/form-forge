import { useCallback, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Building2, Copy } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorBanner } from '@/components/shared/ErrorBanner'
import { cn } from '@/lib/utils'
import { useTenantsQuery } from './useTenantsQuery'
import { CreateTenantForm } from './CreateTenantForm'
import { useLogoutMutation } from '../../auth/authMutations'
import type { CreateTenantResponse, TenantStatus } from './types'

const PAGE_SIZE = 25

// Story 12.5 — standalone platform-super-admin page, deliberately NOT nested inside
// _app/AdminLayout's tab-strip nav (Boundaries): this session cannot load that shell
// at all (no tenantId claim, no refresh token). Composes its own minimal chrome
// instead, and list loading/error/empty states via ErrorBanner + Skeleton (Story 6.11
// convention) exactly like the tenant-admin list pages (roles.tsx/users.tsx) do.
export function TenantsPage() {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [showCreate, setShowCreate] = useState(false)
  const [justCreated, setJustCreated] = useState<CreateTenantResponse | null>(null)
  const tenantsQuery = useTenantsQuery(page, PAGE_SIZE)
  const logoutMutation = useLogoutMutation()

  const totalPages = Math.max(1, tenantsQuery.data?.totalPages ?? 1)

  const handleCreated = useCallback((response: CreateTenantResponse) => {
    setJustCreated(response)
  }, [])

  const handleLogout = useCallback(() => logoutMutation.mutate(), [logoutMutation])

  if (tenantsQuery.isLoading && !tenantsQuery.data) {
    return (
      <PageShell onLogout={handleLogout}>
        <div className="space-y-3">
          <Skeleton className="h-9 w-64" />
          <Skeleton className="h-32 w-full" />
        </div>
      </PageShell>
    )
  }

  if (tenantsQuery.isError && !tenantsQuery.data) {
    return (
      <PageShell onLogout={handleLogout}>
        <ErrorBanner error={tenantsQuery.error} onRetry={() => void tenantsQuery.refetch()} />
      </PageShell>
    )
  }

  const showEmpty = !!tenantsQuery.data && tenantsQuery.data.data.length === 0

  return (
    <PageShell onLogout={handleLogout}>
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <Building2 className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('admin.tenants.title')}</h1>
          </div>
          <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">{t('admin.tenants.subtitle')}</p>
        </div>
        <Button onClick={() => setShowCreate((s) => !s)}>{t('admin.tenants.createButton')}</Button>
      </div>

      {showCreate && <CreateTenantForm onDone={() => setShowCreate(false)} onCreated={handleCreated} />}

      {justCreated && (
        <div
          role="status"
          className="space-y-2 rounded-xl border border-primary/30 bg-primary/5 p-4 text-sm"
        >
          <p className="font-medium text-foreground">
            {t('admin.tenants.createSuccess', { name: justCreated.tenant.name })}
          </p>
          <p className="text-muted-foreground">{t('admin.tenants.temporaryPasswordNotice')}</p>
          <div className="flex items-center gap-2">
            <p className="flex-1 rounded bg-muted px-2 py-1.5 font-mono text-sm break-all">
              {justCreated.temporaryPassword}
            </p>
            <CopyPasswordButton password={justCreated.temporaryPassword} />
          </div>
          <Button variant="ghost" size="sm" onClick={() => setJustCreated(null)}>
            {t('admin.tenants.dismissButton')}
          </Button>
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-5 py-3">{t('admin.tenants.nameLabel')}</th>
                <th className="px-5 py-3">{t('admin.tenants.schemaNameLabel')}</th>
                <th className="px-5 py-3">{t('admin.tenants.statusLabel')}</th>
                <th className="px-5 py-3">{t('admin.tenants.createdAtLabel')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {showEmpty && (
                <tr>
                  <td colSpan={4} className="px-5 py-16 text-center text-sm text-muted-foreground">
                    {t('admin.tenants.noTenants')}
                  </td>
                </tr>
              )}
              {tenantsQuery.data?.data.map((tenant) => (
                <tr key={tenant.id} className="hover:bg-overlay-hover">
                  <td className="px-5 py-3 font-medium text-foreground">{tenant.name}</td>
                  <td className="px-5 py-3 font-mono text-xs text-muted-foreground">{tenant.schemaName}</td>
                  <td className="px-5 py-3">
                    <StatusBadge status={tenant.status} />
                  </td>
                  <td className="px-5 py-3 text-muted-foreground">
                    {new Date(tenant.createdAt).toLocaleString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {!!tenantsQuery.data && tenantsQuery.data.data.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => setPage((p) => p - 1)}
            disabled={page <= 1 || tenantsQuery.isFetching}
          >
            {t('admin.tenants.previousPage')}
          </Button>
          <span>{t('admin.tenants.pageIndicator', { page, totalPages })}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => setPage((p) => p + 1)}
            disabled={page >= totalPages || tenantsQuery.isFetching}
          >
            {t('admin.tenants.nextPage')}
          </Button>
        </nav>
      )}
    </PageShell>
  )
}

interface PageShellProps {
  children: ReactNode
  onLogout: () => void
}

function PageShell({ children, onLogout }: PageShellProps) {
  const { t } = useTranslation()
  return (
    <div className="min-h-screen bg-muted/30 px-4 py-8 sm:px-8">
      <div className="mx-auto max-w-5xl space-y-6">
        <header className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Building2 className="h-5 w-5 text-primary" aria-hidden />
            <span className="text-lg font-semibold text-foreground">{t('nav.brandName')}</span>
          </div>
          <Button variant="ghost" size="sm" onClick={onLogout}>
            {t('auth.logout.button')}
          </Button>
        </header>
        {children}
      </div>
    </div>
  )
}

function CopyPasswordButton({ password }: { password: string }) {
  const { t } = useTranslation()

  const handleCopy = useCallback(() => {
    // Defensive: clipboard.writeText is unavailable outside a secure context (rare
    // for an admin tool, but this text is sensitive enough to guard rather than throw).
    if (!navigator.clipboard?.writeText) {
      toast.error(t('admin.tenants.copyPasswordError'))
      return
    }
    navigator.clipboard.writeText(password).then(
      () => toast.success(t('admin.tenants.copyPasswordSuccess')),
      () => toast.error(t('admin.tenants.copyPasswordError')),
    )
  }, [password, t])

  return (
    <Button
      type="button"
      variant="outline"
      size="sm"
      onClick={handleCopy}
      aria-label={t('admin.tenants.copyPasswordButton')}
    >
      <Copy className="h-4 w-4" />
      {t('admin.tenants.copyPasswordButton')}
    </Button>
  )
}

function StatusBadge({ status }: { status: TenantStatus }) {
  const { t } = useTranslation()
  const styles: Record<TenantStatus, string> = {
    Provisioning: 'bg-amber-100 text-amber-800',
    Active: 'bg-emerald-100 text-emerald-800',
    Suspended: 'bg-muted text-muted-foreground',
    Error: 'bg-destructive/15 text-destructive',
  }
  const labels: Record<TenantStatus, string> = {
    Provisioning: t('admin.tenants.statusProvisioning'),
    Active: t('admin.tenants.statusActive'),
    Suspended: t('admin.tenants.statusSuspended'),
    Error: t('admin.tenants.statusError'),
  }
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium',
        styles[status],
      )}
    >
      {labels[status]}
    </span>
  )
}
