import { useTranslation } from 'react-i18next'
import { ArrowDown, ArrowUp } from 'lucide-react'
import { cn } from '@/lib/utils'

// Single-column sort encoded as "field:dir" in the URL (e.g. "displayName:desc").
// The backend whitelists `field`, so an arbitrary colKey here is harmless.
// Cycle on click: other-column → asc → desc → cleared.
function nextSort(current: string | undefined, colKey: string): string | undefined {
  const [field, dir] = current ? current.split(':') : []
  if (field !== colKey) return `${colKey}:asc`
  if (dir === 'asc') return `${colKey}:desc`
  return undefined
}

export function SortHeader({
  colKey,
  label,
  sort,
  onSort,
  className,
}: {
  colKey: string
  label: string
  sort: string | undefined
  onSort: (next: string | undefined) => void
  className?: string
}) {
  const { t } = useTranslation()
  const [field, dir] = sort ? sort.split(':') : []
  const active = field === colKey
  return (
    <button
      type="button"
      onClick={() => onSort(nextSort(sort, colKey))}
      aria-sort={active ? (dir === 'desc' ? 'descending' : 'ascending') : 'none'}
      className={cn(
        'flex items-center gap-1 text-left text-[11px] font-semibold uppercase tracking-wider text-muted-foreground hover:text-foreground',
        className,
      )}
    >
      {label}
      {active && dir === 'asc' && (
        <ArrowUp className="h-3 w-3" aria-label={t('common.sortAscending')} />
      )}
      {active && dir === 'desc' && (
        <ArrowDown className="h-3 w-3" aria-label={t('common.sortDescending')} />
      )}
    </button>
  )
}
