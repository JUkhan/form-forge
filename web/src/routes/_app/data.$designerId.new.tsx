import { createFileRoute } from '@tanstack/react-router'
import { DataEntryPage } from '@/components/dataEntry/DataEntryPage'

// Story 6.9 — dedicated create-record route. The list route
// (`data.$designerId.tsx`) now hosts the record list; this route hosts the
// DynamicComponent form bound to a real POST /api/data/{designerId} mutation
// via `useCreateRecord`.
export const Route = createFileRoute('/_app/data/$designerId/new')({
  component: RouteComponent,
})

// react-refresh/only-export-components fires here because this file also
// exports a non-component (`Route`). Every file-based route in this repo
// has the same shape; suppress the rule on this internal component.
// eslint-disable-next-line react-refresh/only-export-components
function RouteComponent() {
  const { designerId } = Route.useParams()
  return <DataEntryPage designerId={designerId} />
}
