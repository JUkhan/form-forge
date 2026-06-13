import type { ReactNode } from 'react'

// Dumb conditional renderer. Stays in components/shared/ — must NOT import from
// features/ (AR-31 import hierarchy: routes → features → components → lib).
// Call sites compute `allowed` via the `usePermission` hook and pass it in.
interface PermissionGateProps {
  allowed: boolean
  children: ReactNode
  fallback?: ReactNode
}

export function PermissionGate({ allowed, children, fallback = null }: PermissionGateProps) {
  return <>{allowed ? children : fallback}</>
}
