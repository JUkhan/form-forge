import { createFileRoute } from '@tanstack/react-router'
import DynamicComponent from '@/components/designer/DynamicComponent'
export const Route = createFileRoute('/_app/')({
  component: HomePage,
})

function HomePage() {
  return (
    <DynamicComponent designerId='index' />
  )
}
