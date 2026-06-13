import { createRootRouteWithContext, Outlet } from '@tanstack/react-router'
import type { QueryClient } from '@tanstack/react-query'
import { Toaster } from 'sonner'
import { ThemeProvider } from '../lib/theme/ThemeProvider'

interface RouterContext {
  queryClient: QueryClient
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
})

function RootLayout() {
  return (
    <ThemeProvider>
      <Outlet />
      {/* Toast surface for success/error feedback from admin mutations.
          Position bottom-right per common SaaS convention. (Story 2.8 P17.) */}
      <Toaster richColors position="bottom-right" />
    </ThemeProvider>
  )
}
