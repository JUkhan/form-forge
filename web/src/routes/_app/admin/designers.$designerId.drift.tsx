import { createFileRoute } from '@tanstack/react-router'
import { SchemaDriftView } from '../../../features/admin/designers/SchemaDriftView'

// Story 5.6 — Admin Schema Drift view. URL: /admin/designers/{designerId}/drift.
// No parent designers.tsx layout route exists yet; this leaf route stands alone
// (deep-linked from the menu detail page once the binding section grows the
// "View schema drift" affordance — out of scope for Story 5.6).
export const Route = createFileRoute('/_app/admin/designers/$designerId/drift')({
  component: SchemaDriftPage,
})

function SchemaDriftPage() {
  const { designerId } = Route.useParams()
  return <SchemaDriftView designerId={designerId} />
}
