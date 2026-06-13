import { createFileRoute, Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { ArrowRight, ClipboardList, FileEdit, ScrollText } from 'lucide-react'

export const Route = createFileRoute('/_app/admin/audit')({
  component: AuditLogsPage,
})

export function AuditLogsPage() {
  const { t } = useTranslation()
  return (
    <div className="space-y-6">
      <div className="min-w-0">
        <div className="flex items-center gap-2.5">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
            <ClipboardList className="h-5 w-5" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight">
            {t('admin.audit.title')}
          </h1>
        </div>
        <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
          {t('admin.audit.subtitle')}
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <AuditCard
          icon={<ScrollText className="h-5 w-5" />}
          title={t('admin.audit.schemaSection')}
          description={t('admin.audit.schemaDesc')}
          linkTo="/designer/library"
          linkLabel={t('admin.audit.goToLibrary')}
        />
        <AuditCard
          icon={<FileEdit className="h-5 w-5" />}
          title={t('admin.audit.mutationSection')}
          description={t('admin.audit.mutationDesc')}
          linkTo="/admin/menus"
          linkLabel={t('admin.audit.goToMenus')}
        />
      </div>
    </div>
  )
}

interface AuditCardProps {
  icon: React.ReactNode
  title: string
  description: string
  linkTo: '/designer/library' | '/admin/menus'
  linkLabel: string
}

function AuditCard({ icon, title, description, linkTo, linkLabel }: AuditCardProps) {
  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border bg-card p-6 shadow-sm transition-shadow hover:shadow-md">
      <div className="flex items-center gap-2.5">
        <div className="flex h-8 w-8 items-center justify-center rounded-md bg-primary/10 text-primary">
          {icon}
        </div>
        <h2 className="text-base font-semibold text-foreground">{title}</h2>
      </div>
      <p className="flex-1 text-sm text-muted-foreground">{description}</p>
      <Link
        to={linkTo}
        className="inline-flex w-fit items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        {linkLabel}
        <ArrowRight className="h-3.5 w-3.5" />
      </Link>
    </section>
  )
}
