import * as Icons from 'lucide-react'
import type { LucideProps } from 'lucide-react'
import type { ComponentType } from 'react'

interface LucideIconProps extends Omit<LucideProps, 'ref'> {
  name: string
}

// Bundle size note: `import * as Icons` pulls all ~3,000 icons. Acceptable on admin-only
// pages; Story 4.7's public navbar must use tree-shaken named imports instead.
export function LucideIcon({ name, size = 16, ...rest }: LucideIconProps) {
  const IconComponent = (Icons as unknown as Record<string, ComponentType<LucideProps>>)[name]
  if (!IconComponent) return null
  return <IconComponent size={size} {...rest} />
}

// P7: client-side validity check so IconPickerSection can disable "Set Lucide Icon"
// for names that produce a blank preview (not a known lucide-react export).
export function isValidLucideName(name: string): boolean {
  return !!(Icons as unknown as Record<string, unknown>)[name]
}
