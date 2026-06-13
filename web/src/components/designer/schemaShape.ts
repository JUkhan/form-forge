import type { DesignerElement } from '../../types/designer'

// Shared schema-shape probe used by DynamicComponent and any host that needs
// to distinguish "schema not found" from "schema parses but is the wrong
// shape" before mounting the renderer. Extracted to its own file so component
// files keep react-refresh/only-export-components compliance.
export function isDesignerElementShape(v: unknown): v is DesignerElement {
  if (typeof v !== 'object' || v === null || Array.isArray(v)) return false
  const o = v as Record<string, unknown>
  return (
    typeof o.type === 'string' &&
    typeof o.properties === 'object' &&
    o.properties !== null &&
    !Array.isArray(o.properties)
  )
}
