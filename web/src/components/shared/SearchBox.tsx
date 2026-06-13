import { useEffect, useRef, useState } from 'react'
import { Search } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

// Debounced search input bound to a URL search param. `value` is the committed
// (URL) value; `onChange` pushes the debounced text. onChange MUST be stable
// (wrap in useCallback) or the debounce timer resets on every parent render.
export function SearchBox({
  value,
  onChange,
  placeholder,
  className,
}: {
  value: string
  onChange: (next: string) => void
  placeholder: string
  className?: string
}) {
  const [text, setText] = useState(value)
  // Tracks the value we know the URL holds — our own last debounced push or an
  // external change we've absorbed. Comparing against it lets us ignore the echo
  // of our own push (which would steal focus mid-typing) and re-seed only on real
  // external changes (back/forward nav, Clear).
  const lastSynced = useRef(value)
  useEffect(() => {
    if (value !== lastSynced.current) {
      setText(value)
      lastSynced.current = value
    }
  }, [value])
  useEffect(() => {
    if (text === lastSynced.current) return
    const id = window.setTimeout(() => {
      lastSynced.current = text
      onChange(text)
    }, 300)
    return () => window.clearTimeout(id)
  }, [text, onChange])

  return (
    <div className={cn('relative w-full sm:w-72', className)}>
      <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
      <Input
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={placeholder}
        aria-label={placeholder}
        className="h-9 pl-8"
      />
    </div>
  )
}
