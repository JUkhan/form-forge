import { createFileRoute } from '@tanstack/react-router'
import { MutationAuditLogView } from '../../../features/admin/data/MutationAuditLogView'

// Story 6.8 — Mutation Audit Log view. URL: /admin/data/{designerId}/audit.
export const Route = createFileRoute('/_app/admin/data/$designerId/audit')({
  component: MutationAuditLogPage,
})

function MutationAuditLogPage() {
  const { designerId } = Route.useParams()
  return <MutationAuditLogView designerId={designerId} />
}
