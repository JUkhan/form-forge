import { useTranslation } from 'react-i18next'
import type { ProvisioningStatus } from '../../features/menu/types'

interface ProvisioningStatusBadgeProps {
  status: ProvisioningStatus | null | undefined
}

// Story 5.2 — terse coloured pill for the menu detail page. Renders nothing for
// unbound menus (status === null) so a section header doesn't show an empty pill.
export function ProvisioningStatusBadge({ status }: ProvisioningStatusBadgeProps) {
  const { t } = useTranslation()
  if (status == null) return null

  const styles: Record<ProvisioningStatus, { bg: string; fg: string; key: string }> = {
    Pending: { bg: '#fff7cc', fg: '#7a5a00', key: 'admin.menus.provisioningPending' },
    Success: { bg: '#d4f4dd', fg: '#1b6b3a', key: 'admin.menus.provisioningSuccess' },
    Error: { bg: '#fad0d0', fg: '#a30000', key: 'admin.menus.provisioningError' },
    // FR-54 — VIEW bindings are never provisioned; muted neutral pill.
    // hsl() wrapper required: shadcn --muted / --muted-foreground are HSL triples.
    NotApplicable: { bg: 'hsl(var(--muted))', fg: 'hsl(var(--muted-foreground))', key: 'admin.menus.provisioningNotApplicable' },
  }
  const spec = styles[status as ProvisioningStatus]
  if (!spec) return null
  // Status badges are atoms; the provisioningError key takes an {{error}} param
  // but here we only display the headline text. Pass empty string so the
  // missing-param interpolation doesn't surface ugly placeholder text.
  const label = status === 'Error' ? t(spec.key, { error: '' }) : t(spec.key)

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
      {label.replace(/:\s*$/, '')}
    </span>
  )
}
