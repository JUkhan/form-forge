import { createFileRoute } from '@tanstack/react-router'
import { RecordDetailPage } from '@/components/dataEntry/RecordDetailPage'

// Story 6.9 — record detail / edit route. URL: /data/$designerId/$recordId.
// Loads the existing record via `useRecord` and renders DynamicComponent with
// `initialData` for edit mode or read-only (no Save) when canUpdate is false.
export const Route = createFileRoute('/_app/data/$designerId/$recordId')({
  component: RouteComponent,
})

// react-refresh/only-export-components fires here because this file also
// exports a non-component (`Route`). Every file-based route in this repo
// has the same shape; suppress the rule on this internal component.
// eslint-disable-next-line react-refresh/only-export-components
function RouteComponent() {
  const { designerId, recordId } = Route.useParams()
  return <RecordDetailPage designerId={designerId} recordId={recordId} />
}
