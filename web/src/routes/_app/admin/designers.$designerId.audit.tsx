import { createFileRoute } from '@tanstack/react-router'
import { SchemaAuditLogView } from '../../../features/admin/designers/SchemaAuditLogView'

// Story 5.7 — Schema Audit Log view. URL: /admin/designers/{designerId}/audit.
// Parallels the drift view at designers.$designerId.drift.tsx (Story 5.6).
export const Route = createFileRoute('/_app/admin/designers/$designerId/audit')({
  component: SchemaAuditLogPage,
})

function SchemaAuditLogPage() {
  const { designerId } = Route.useParams()
  return <SchemaAuditLogView designerId={designerId} />
}
