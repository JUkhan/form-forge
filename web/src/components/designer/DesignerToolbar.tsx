import { useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import {
  AlignLeft,
  CalendarDays,
  CheckSquare,
  ChevronDown,
  Code,
  Columns3,
  FileText,
  Hash,
  Image as ImageIcon,
  LayoutList,
  LayoutPanelTop,
  ListTree,
  MousePointerClick,
  Palette,
  Paperclip,
  Rows3,
  Table2,
  TextCursorInput,
  Type,
  Variable,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import { Command, CommandInput } from '@/components/ui/command'
import { DRAG_MIME, type DragData } from './dnd'
import type { UseKeyboardDnDReturn } from './useKeyboardDnD'

type StructuralType = 'Stack' | 'Row' | 'Tabs' | 'DatasetComponent' | 'SingleRecord'
type LeafType =
  | 'Label'
  | 'Button'
  | 'Text Input'
  | 'TextArea'
  | 'Number Input'
  | 'Checkbox'
  | 'Dropdown'
  | 'DateTime Picker'
  | 'Color Picker'
  | 'Repeater'
  | 'Repeater Field'
  | 'TreeView'
  | 'Image'
  | 'File'
  | 'RawHtml'

type PaletteType = StructuralType | LeafType

const STRUCTURAL_ELEMENTS: ReadonlyArray<{
  type: StructuralType
  label: string
  icon: typeof Rows3
  description: string
}> = [
  { type: 'Stack', label: 'Stack', icon: Rows3,          description: 'Vertical container' },
  { type: 'Row',   label: 'Row',   icon: Columns3,       description: 'Horizontal container' },
  { type: 'Tabs',  label: 'Tabs',  icon: LayoutPanelTop, description: 'Tabbed container' },
  { type: 'DatasetComponent', label: 'Dataset', icon: Table2, description: 'Dataset data view (table + filter)' },
  { type: 'SingleRecord', label: 'Single Record', icon: FileText, description: 'Add/edit one record per user from a CRUD designer' },
]

const LEAF_ELEMENTS: ReadonlyArray<{
  type: LeafType
  label: string
  icon: typeof Rows3
  description: string
}> = [
  { type: 'Label',           label: 'Label',           icon: Type,              description: 'Static text label' },
  { type: 'Button',          label: 'Button',          icon: MousePointerClick, description: 'Clickable button' },
  { type: 'Text Input',      label: 'Text Input',      icon: TextCursorInput,   description: 'Single-line text field' },
  { type: 'TextArea',        label: 'Text Area',       icon: AlignLeft,         description: 'Multi-line text field' },
  { type: 'Number Input',    label: 'Number Input',    icon: Hash,              description: 'Numeric input field' },
  { type: 'Checkbox',        label: 'Checkbox',        icon: CheckSquare,       description: 'Boolean toggle' },
  { type: 'Dropdown',        label: 'Dropdown',        icon: ChevronDown,       description: 'Single-choice list' },
  { type: 'DateTime Picker', label: 'DateTime Picker', icon: CalendarDays,      description: 'Date or datetime field' },
  { type: 'Color Picker',    label: 'Color Picker',    icon: Palette,           description: 'Color selector' },
  { type: 'Repeater',        label: 'Repeater',        icon: LayoutList,        description: 'Repeating row group' },
  { type: 'Repeater Field',  label: 'Field',           icon: Variable,          description: 'Display a form/record value, optionally mapped or shown as a table column' },
  { type: 'TreeView',        label: 'Tree View',       icon: ListTree,          description: 'Self-referencing tree of a node template (lazy, paginated)' },
  { type: 'Image',           label: 'Image',           icon: ImageIcon,         description: 'Image preview' },
  { type: 'File',            label: 'File',            icon: Paperclip,         description: 'File upload field' },
  { type: 'RawHtml',         label: 'Raw HTML',        icon: Code,              description: 'Render raw HTML markup' },
]

function PaletteCard({
  type,
  label,
  description,
  Icon,
  kbdDnD,
}: {
  type: PaletteType
  label: string
  description: string
  Icon: typeof Rows3
  kbdDnD?: UseKeyboardDnDReturn<DragData>
}) {
  const { t } = useTranslation()
  // ARIA 1.1 pattern: this card has role="button" and serves as the keyboard
  // pickup target. aria-pressed reflects "this specific palette item is the
  // currently picked-up element"; the page-level aria-live region announces
  // the state change. aria-grabbed was deprecated in ARIA 1.1 / removed in
  // ARIA 1.2 (axe-core flags it as a critical violation).
  const isGrabbed =
    kbdDnD?.pickedUp?.source === 'PALETTE' &&
    kbdDnD.pickedUp.elementType === type
  return (
    <div
      draggable
      onDragStart={(e) => {
        const data: DragData = { source: 'PALETTE', elementType: type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'copy'
      }}
      onKeyDown={(e) => {
        // Skip everything when the page hasn't wired keyboard DnD — without
        // this guard, Space on the role="button" would still preventDefault
        // (eating the keystroke) but no pickUp would fire.
        if (!kbdDnD) return
        if (e.key === ' ' || e.key === 'Enter') {
          // Guard: if a pickup is already active, ignore further palette
          // Space/Enter so the user can't silently swap their pickup mid-flow.
          if (kbdDnD.pickedUp) return
          e.preventDefault()
          kbdDnD.pickUp(
            { source: 'PALETTE', elementType: type, displayLabel: label },
            t('designer.keyboard.pickedUp', { type: label }),
            e.currentTarget as HTMLElement,
          )
        }
        if (e.key === 'Escape' && kbdDnD.pickedUp) {
          kbdDnD.cancel(t('designer.keyboard.cancelled'))
        }
      }}
      tabIndex={0}
      className={cn(
        'flex cursor-grab items-center gap-3 rounded-lg border bg-card px-3 py-2.5 text-sm shadow-xs transition-colors',
        'border-border hover:border-foreground/30 hover:bg-overlay-hover active:bg-overlay-active',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
      role="button"
      aria-label={`Drag ${label} onto the canvas`}
      aria-pressed={kbdDnD ? isGrabbed : undefined}
    >
      <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
        <Icon className="h-4 w-4" />
      </div>
      <div className="min-w-0">
        <p className="truncate font-medium text-foreground">{label}</p>
        <p className="truncate text-xs text-muted-foreground">{description}</p>
      </div>
    </div>
  )
}

interface DesignerToolbarProps {
  className?: string
  kbdDnD?: UseKeyboardDnDReturn<DragData>
}

export default function DesignerToolbar({ className, kbdDnD }: DesignerToolbarProps) {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')

  // Case-insensitive substring match across the user-visible label, the
  // description, and the internal palette type (so searching "datetime" or
  // "html" hits regardless of which field carries the term). Manual filtering
  // — rather than cmdk's CommandItem registry — keeps each PaletteCard's
  // keyboard DnD (Space/Enter pickup, focus management) fully intact; cmdk's
  // arrow/Enter list navigation would otherwise contend with it.
  const filter = useMemo(() => {
    const q = query.trim().toLowerCase()
    return <T extends { label: string; description: string; type: string }>(
      items: ReadonlyArray<T>,
    ) =>
      q === ''
        ? items
        : items.filter(
            (el) =>
              el.label.toLowerCase().includes(q) ||
              el.description.toLowerCase().includes(q) ||
              el.type.toLowerCase().includes(q),
          )
  }, [query])

  const structural = filter(STRUCTURAL_ELEMENTS)
  const leaf = filter(LEAF_ELEMENTS)
  const hasResults = structural.length > 0 || leaf.length > 0

  return (
    <aside
      className={cn(
        'flex w-60 shrink-0 flex-col border-r border-border bg-muted/60',
        className,
      )}
    >
      {/* Command provides the styled search shell. shouldFilter is off because
          we filter the lists ourselves and render no CommandItems — the cards
          own their drag/keyboard behaviour. */}
      <Command
        shouldFilter={false}
        className="flex flex-1 flex-col overflow-hidden rounded-none bg-transparent"
      >
        <div className="shrink-0 p-3 pb-0">
          <CommandInput
            value={query}
            onValueChange={setQuery}
            placeholder={t('designer.toolbar.searchPlaceholder')}
          />
        </div>
        <div className="flex flex-1 flex-col overflow-y-auto">
          {!hasResults ? (
            <p className="px-4 py-6 text-center text-sm text-muted-foreground">
              {t('designer.toolbar.noResults', { query: query.trim() })}
            </p>
          ) : (
            <>
              {structural.length > 0 && (
                <>
                  <ToolbarSectionHeader>
                    {t('designer.toolbar.structuralHeader')}
                  </ToolbarSectionHeader>
                  <div className="flex flex-col gap-2 p-3">
                    {structural.map((el) => (
                      <PaletteCard
                        key={el.type}
                        type={el.type}
                        label={el.label}
                        description={el.description}
                        Icon={el.icon}
                        kbdDnD={kbdDnD}
                      />
                    ))}
                  </div>
                </>
              )}
              {leaf.length > 0 && (
                <>
                  <ToolbarSectionHeader>
                    {t('designer.toolbar.leafHeader')}
                  </ToolbarSectionHeader>
                  <div className="flex flex-col gap-2 p-3">
                    {leaf.map((el) => (
                      <PaletteCard
                        key={el.type}
                        type={el.type}
                        label={el.label}
                        description={el.description}
                        Icon={el.icon}
                        kbdDnD={kbdDnD}
                      />
                    ))}
                  </div>
                </>
              )}
            </>
          )}
        </div>
      </Command>
    </aside>
  )
}

function ToolbarSectionHeader({ children }: { children: ReactNode }) {
  return (
    <div className="sticky top-0 z-10 border-y border-border bg-muted/95 px-4 py-2 backdrop-blur-sm first:border-t-0">
      <h2 className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
        {children}
      </h2>
    </div>
  )
}
