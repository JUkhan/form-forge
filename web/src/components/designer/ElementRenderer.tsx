import RepeaterRowDrawer from './RepeaterRowDrawer'
import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, ImageOff, Paperclip, Pencil, Trash2, Upload, X } from 'lucide-react'
import { getFieldKey, type DesignerElement } from '@/types/designer'
import { filesApi } from '@/features/designer/filesApi'
import { cn } from '@/lib/utils'
import { Combobox, type ComboboxOption } from '@/components/ui/combobox'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { fetchDropdownOptions } from '@/features/data-entry/dropdownOptionsApi'
import { compileMapExpression } from '@/features/data-entry/mapExpression'
import { fetchDatasetOptions } from '@/features/datasets/datasetApi'
import { httpClient } from '@/features/auth/httpClient'
import DatasetView from './DatasetView'
import SingleRecordView from './SingleRecordView'
import TreeViewRenderer from './TreeView'
import { resolveTextInputType } from './textInputTypes'
import { subtreeHasErrors } from './validation'
import { extractOptions, parseDependsOn, parseDependsOnMap, resolveApiPath } from './dynamicDropdown'

const CONTAINER_TYPES = new Set(['Stack', 'Row', 'Tabs', 'Repeater', 'TreeView', 'DatasetComponent'])

const LEAF_BASE =
  'h-9 rounded-lg border border-field-border px-3 py-2 text-sm text-foreground bg-field w-full placeholder:text-placeholder'

const HEX_COLOR = /^#[0-9a-f]{6}$/i

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

// Parse a `key: value; key: value` CSS string into a React style object. Accepts
// either kebab-case ("font-size") or camelCase ("fontSize") property names — kebab
// is converted on the way in. Invalid declarations (no colon, empty halves) are
// dropped silently. Used by the Repeater Field leaf so authors can paste arbitrary
// inline CSS into the inspector. React does NOT sanitize CSS values, so we apply
// a small blocklist below to reject UI-overlay / clickjack-flavored declarations
// and `url(javascript:…)`. CSS custom properties (`--*`) pass through verbatim.
//
// Splitting on a literal `;` is unsafe — semicolons appear inside `url(...)` and
// quoted string values. We hand-walk the string instead, tracking paren depth
// and quote state, and only treat a `;` as a declaration terminator when both
// are zero/false.
// Reject overlay/clickjack-flavored CSS. The blocklist covers properties that
// let a Repeater Field leaf escape its container or capture clicks outside its
// own bounds:
//   - position: any non-static value (fixed/absolute/sticky/relative) lets the
//     leaf paint outside the row;
//   - top/left/right/bottom/inset: required to position an escaped element;
//   - transform: translate to off-screen / overlap parent overlay;
//   - z-index: stack above modal/dialog chrome;
//   - pointer-events: none disables clicks on parent UI underneath.
// A schema author who legitimately needs these can use Tailwind classes via
// styleClasses — Tailwind generates the same CSS but the surface is bounded
// to the project's whitelisted class set.
const BLOCKED_DECL_PROPS = new Set([
  'position',
  'top',
  'left',
  'right',
  'bottom',
  'inset',
  'inset-block',
  'inset-block-start',
  'inset-block-end',
  'inset-inline',
  'inset-inline-start',
  'inset-inline-end',
  'transform',
  'translate',
  'z-index',
  'pointer-events',
])
// CSS allows hex/unicode escapes inside identifiers and string values (e.g.
// `\6Aavascript:` decodes to `javascript:`). A simple regex on the raw text
// would miss that. Decode CSS escapes before checking the URL scheme.
const CSS_ESCAPE_RE = /\\([0-9a-fA-F]{1,6})\s?|\\(.)/g
function decodeCssEscapes(s: string): string {
  return s.replace(CSS_ESCAPE_RE, (_m, hex: string | undefined, lit: string | undefined) => {
    if (hex !== undefined) {
      const cp = parseInt(hex, 16)
      try {
        return String.fromCodePoint(cp)
      } catch {
        return ''
      }
    }
    return lit ?? ''
  })
}
const URL_SCHEME_RE = /url\s*\(\s*['"]?\s*([a-z][a-z0-9+.-]*):/i
const URL_ALLOWED_SCHEMES = new Set(['http', 'https'])
function valueHasDangerousUrl(val: string): boolean {
  const decoded = decodeCssEscapes(val)
  const m = URL_SCHEME_RE.exec(decoded)
  if (!m) return false
  return !URL_ALLOWED_SCHEMES.has(m[1].toLowerCase())
}

function splitDeclarations(css: string): string[] {
  const out: string[] = []
  let buf = ''
  let parens = 0
  let quote: '"' | "'" | null = null
  for (let i = 0; i < css.length; i++) {
    const ch = css[i]
    if (quote) {
      buf += ch
      if (ch === '\\' && i + 1 < css.length) {
        buf += css[++i]
      } else if (ch === quote) {
        quote = null
      }
      continue
    }
    if (ch === '"' || ch === "'") {
      quote = ch
      buf += ch
      continue
    }
    if (ch === '(') parens++
    else if (ch === ')' && parens > 0) parens--
    if (ch === ';' && parens === 0) {
      if (buf.trim() !== '') out.push(buf)
      buf = ''
      continue
    }
    buf += ch
  }
  if (buf.trim() !== '') out.push(buf)
  return out
}

function parseInlineStyle(css: string): Record<string, string> {
  const out: Record<string, string> = {}
  if (!css || typeof css !== 'string') return out
  for (const decl of splitDeclarations(css)) {
    const idx = decl.indexOf(':')
    if (idx === -1) continue
    const key = decl.slice(0, idx).trim()
    const val = decl.slice(idx + 1).trim()
    if (key === '' || val === '') continue

    // Reject overlay-style positioning that lets a row leaf escape its container
    // (clickjack/UI-redress). A schema author who genuinely needs these can do
    // it via Tailwind classes on styleClasses; the inspector copy steers them
    // there.
    const lcKey = key.toLowerCase()
    if (BLOCKED_DECL_PROPS.has(lcKey)) continue
    if (valueHasDangerousUrl(val)) continue

    // CSS custom properties (`--my-var`) must keep their leading dashes — pass them
    // through verbatim and let React render them as-is.
    if (key.startsWith('--')) {
      out[key] = val
      continue
    }
    // `-ms-` is the only vendor prefix React expects in lowercase-first form
    // (msTransform, not MsTransform). Other vendor prefixes capitalize the
    // leading letter (WebkitTransform, MozTransform).
    const reactKey = lcKey.startsWith('-ms-')
      ? 'ms' + lcKey.slice(4).replace(/-([a-z])/g, (_m, c: string) => c.toUpperCase())
      : key.replace(/-([a-z])/g, (_m, c: string) => c.toUpperCase())
    out[reactKey] = val
  }
  return out
}

export type InteractiveFormProps = {
  formData: Record<string, unknown>
  onChange: (bindTo: string, value: unknown) => void
  onPrimaryButtonClick: () => void
  errorByBindTo?: Map<string, string>
  formIsValid?: boolean
  // Element ids that are conditionally hidden by hideWhenAll / hideWhenAny.
  // Honored only in interactive mode — the designer canvas always shows
  // every element so authors can edit them.
  hiddenElementIds?: Set<string>
}

const ERROR_INPUT_CLASS = 'border-destructive ring-1 ring-destructive/40'
const ERROR_MESSAGE_CLASS = 'mt-1 text-xs text-destructive'

function trimmedLabel(element: DesignerElement): string {
  const raw = element.properties.label
  return typeof raw === 'string' ? raw.trim() : ''
}

// Returns the fallback aria-label only when no visible <label> will render — when a visible
// label is present, omitting aria-label avoids duplicating the accessible name (WCAG 1.3.1).
function ariaFallback(element: DesignerElement, fallback: string): string | undefined {
  return trimmedLabel(element) === '' ? fallback : undefined
}

// id is only emitted when the renderer is interactive AND a visible <label> will render.
// Non-interactive canvas previews don't claim DOM ids — clicking a <label> whose target is
// disabled is a no-op anyway, and dropping the id avoids duplicate DOM ids when the canvas
// and an interactive ComponentPreviewModal mount the same element tree concurrently.
function inputIdFor(element: DesignerElement, interactive: boolean): string | undefined {
  return interactive && trimmedLabel(element) !== '' ? element.id : undefined
}

function LabeledField({
  element,
  interactive,
  children,
}: {
  element: DesignerElement
  interactive: boolean
  children: ReactNode
}) {
  const label = trimmedLabel(element)
  const mode = element.properties.labelMode === 'horizontal' ? 'horizontal' : 'vertical'
  // Even when no visible <label> is rendered, wrap children in a column-flex container so
  // the input + error <p> stack vertically — without this wrapper, leaves placed inside a
  // Row container (flex flex-row) would render the error <p> inline beside the input.
  if (label === '') return <div className="flex w-full flex-col gap-1">{children}</div>
  // htmlFor is paired with the input's id, which inputIdFor only emits in interactive mode.
  const labelHtmlFor = interactive ? element.id : undefined
  if (mode === 'horizontal') {
    return (
      <div className="flex w-full items-start gap-2">
        <label
          htmlFor={labelHtmlFor}
          className="min-w-[120px] pt-1.5 text-left text-xs font-medium text-foreground"
        >
          {label}
        </label>
        <div className="flex flex-1 flex-col">{children}</div>
      </div>
    )
  }
  return (
    <div className="flex w-full flex-col gap-1">
      <label htmlFor={labelHtmlFor} className="text-left text-xs font-medium text-foreground">
        {label}
      </label>
      {children}
    </div>
  )
}

export default function ElementRenderer({
  element,
  interactive = false,
  interactiveProps,
  parentType,
}: {
  element: DesignerElement
  interactive?: boolean
  interactiveProps?: InteractiveFormProps
  // parentType is the element.type of the immediate parent in the tree. The Stack
  // branch reads it to decide whether to honor `contentGapPx` (only when the
  // immediate parent is `Tabs`, i.e. this Stack is acting as a tab panel).
  parentType?: string
}) {
  // Sibling subtrees should not re-render when an unrelated leaf's properties change.
  // Keyed on the element reference: cloneTree in the store produces a new reference
  // whenever this element's id, type, properties, or children are touched.
  return useMemo(
    () => {
      // Conditional visibility: hideWhenAll / hideWhenAny are resolved once per
      // form render by DynamicComponent (see visibility.ts) and the resulting
      // id set is threaded through here. Returning null excludes the element
      // from layout entirely — children of hidden containers also disappear
      // because their parent never renders to drive a recursive ElementRenderer.
      // Non-interactive paths (canvas preview, Repeater list row display)
      // intentionally bypass this so the author/reader still sees every element.
      if (interactive && interactiveProps?.hiddenElementIds?.has(element.id)) {
        return null
      }
      return <ElementBody element={element} interactive={interactive} interactiveProps={interactiveProps} parentType={parentType} />
    },
    [element, interactive, interactiveProps, parentType],
  )
}

function ElementBody({
  element,
  interactive,
  interactiveProps,
  parentType,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
  parentType?: string
}) {
  if (element.type === 'Stack') {
    // contentGapPx is a per-tab property authored via PropertyInspector — it is
    // only meaningful when this Stack is the active tab panel of a Tabs
    // container. A Stack reparented OUT of Tabs would otherwise carry a stale
    // inline `gap` with no inspector recourse, so we gate on parentType.
    let gapStyle: { gap: string } | undefined
    if (parentType === 'Tabs') {
      const gapRaw = element.properties.contentGapPx
      const gapNum = gapRaw !== undefined ? Number(gapRaw) : NaN
      if (Number.isFinite(gapNum)) {
        gapStyle = { gap: `${gapNum < 0 ? 0 : gapNum}px` }
      }
    }
    return (
      <div
        className={cn(
          'flex flex-col gap-4',
          typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
        )}
        style={gapStyle}
      >
        {(element.children ?? []).map((c) => (
          <ElementRenderer key={c.id} element={c} interactive={interactive} interactiveProps={interactiveProps} parentType="Stack" />
        ))}
      </div>
    )
  }
  if (element.type === 'Row') {
    return (
      <div
        className={cn(
          'flex flex-row gap-4',
          typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
        )}
      >
        {(element.children ?? []).map((c) => (
          <ElementRenderer key={c.id} element={c} interactive={interactive} interactiveProps={interactiveProps} parentType="Row" />
        ))}
      </div>
    )
  }
  if (element.type === 'Tabs') return <TabsRenderer element={element} interactive={interactive} interactiveProps={interactiveProps} />
  // Repeater is a CONTAINER_TYPES member (its template children sit in `children`),
  // but it has its own dedicated renderer with list/template/modal/preview chrome.
  if (element.type === 'Repeater') return <RepeaterRenderer element={element} interactive={interactive} interactiveProps={interactiveProps} />
  // TreeView extends Repeater but renders a standalone self-referencing tree of the
  // selected node template (rowDesignerId). It fetches + mutates live data at runtime,
  // so the canvas shows a static preview; the interactive runtime renders the tree.
  if (element.type === 'TreeView')
    return <TreeViewRenderer element={element} interactive={interactive} interactiveProps={interactiveProps} />

  // DatasetComponent is a self-contained data view (toolbar + table + filter sub-form). It
  // fetches live data, so render it only in the interactive runtime; previews show a stub.
  if (element.type === 'DatasetComponent') {
    return interactive ? (
      <DatasetView element={element} />
    ) : (
      <div className="flex h-16 w-full items-center justify-center rounded-md border border-dashed border-border bg-muted text-xs text-muted-foreground">
        Dataset view (renders at runtime)
      </div>
    )
  }
  // SingleRecord embeds another CRUD designer's form and reads/writes one per-user record.
  // It fetches live data + renders a nested DynamicComponent, so only render it in the
  // interactive runtime; the designer canvas / previews show a stub.
  if (element.type === 'SingleRecord') {
    return interactive ? (
      <SingleRecordView element={element} />
    ) : (
      <div className="flex h-16 w-full items-center justify-center rounded-md border border-dashed border-border bg-muted text-xs text-muted-foreground">
        Single record form (renders at runtime)
      </div>
    )
  }
  if (CONTAINER_TYPES.has(element.type)) return null
  return <LeafRenderer element={element} interactive={interactive} interactiveProps={interactiveProps} />
}

function TabsRenderer({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  const { t } = useTranslation()
  // Track active tab by child id, not positional index — visibility filtering
  // shifts indexes when an earlier tab is hidden, which would otherwise silently
  // slide the user's selection to a different tab (and back again when the
  // hidden tab reappears).
  const [activeTabId, setActiveTabId] = useState<string | null>(null)
  // Per-mount unique prefix so two DynamicComponent instances mounted in the
  // same page (e.g. modal + main form) don't collide on the tab↔panel id
  // pair used for aria-labelledby. Story 7.4: tablist/tab/tabpanel roles.
  const tabsIdPrefix = useId()
  const rawChildren = element.children ?? []
  // In interactive mode, filter tab panels that have been conditionally hidden so
  // the tab button doesn't render either — leaving an orphan button that opens an
  // empty panel would be the worst-of-both-worlds.
  const hiddenIds = interactive ? interactiveProps?.hiddenElementIds : undefined
  const children = hiddenIds
    ? rawChildren.filter((c) => !hiddenIds.has(c.id))
    : rawChildren
  const activeChild =
    (activeTabId !== null && children.find((c) => c.id === activeTabId)) || children[0] || null
  const safeIndex = activeChild ? children.findIndex((c) => c.id === activeChild.id) : -1
  const errorByBindTo = interactiveProps?.errorByBindTo
  const orientation =
    String(element.properties.orientation ?? 'horizontal') === 'vertical' ? 'vertical' : 'horizontal'
  // paddingPx defaults to 8 (same as createNewElement / +Add Tab seed). Negative
  // clamps to 0 since negative CSS padding renders as visually broken.
  const paddingRaw = activeChild ? Number(activeChild.properties.paddingPx ?? 8) : 8
  const padding = !Number.isFinite(paddingRaw) ? 8 : paddingRaw < 0 ? 0 : paddingRaw

  function tabLabel(child: DesignerElement, i: number): string {
    const raw = child.properties.tabName
    if (raw === undefined || raw === null) return t('designer.canvas.tabFallbackName', { n: i + 1 })
    const s = String(raw).trim()
    return s === '' ? t('designer.canvas.tabFallbackName', { n: i + 1 }) : s
  }

  const tabId = (childId: string) => `${tabsIdPrefix}-tab-${childId}`
  const panelId = (childId: string) => `${tabsIdPrefix}-panel-${childId}`
  const activeTabIdAttr = activeChild ? tabId(activeChild.id) : undefined
  const activePanelIdAttr = activeChild ? panelId(activeChild.id) : undefined

  if (orientation === 'vertical') {
    return (
      <div
        className={cn(
          'flex overflow-hidden rounded-md border border-dashed border-border',
          typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
        )}
      >
        <div
          role="tablist"
          aria-orientation="vertical"
          className="flex flex-col border-r border-border bg-muted min-w-[140px]"
        >
          {children.map((c, i) => {
            const hasErrors = interactive && subtreeHasErrors(c, errorByBindTo)
            const label = tabLabel(c, i)
            const isActive = i === safeIndex
            return (
              <button
                key={c.id}
                type="button"
                role="tab"
                id={tabId(c.id)}
                aria-selected={isActive}
                aria-controls={isActive ? activePanelIdAttr : undefined}
                onClick={() => setActiveTabId(c.id)}
                aria-label={hasErrors ? `${label} (has validation errors)` : label}
                className={cn(
                  'flex items-center gap-1.5 px-3 py-2 text-left text-xs font-medium transition-colors',
                  isActive
                    ? 'border-l-2 border-primary text-primary bg-card'
                    : 'text-muted-foreground hover:text-foreground',
                )}
              >
                <span>{label}</span>
                {hasErrors && (
                  <span
                    className="inline-block h-1.5 w-1.5 rounded-full bg-destructive"
                    aria-hidden="true"
                  />
                )}
              </button>
            )
          })}
        </div>
        <div
          className="flex-1"
          style={{ padding: `${padding}px` }}
          role="tabpanel"
          id={activePanelIdAttr}
          aria-labelledby={activeTabIdAttr}
        >
          {activeChild && (
            <ElementRenderer element={activeChild} interactive={interactive} interactiveProps={interactiveProps} parentType="Tabs" />
          )}
        </div>
      </div>
    )
  }

  return (
    <div
      className={cn(
        // min-w-0 lets this container shrink below its tab-strip's intrinsic
        // width when it sits inside a flex parent (Stack/Row), so a long row
        // of tabs scrolls instead of blowing past the host's right edge.
        // overflow-hidden clips the tab-strip's bg-muted fill to the rounded
        // top corners (Radix popovers/dropdowns portal out, so they're unaffected).
        'min-w-0 overflow-hidden rounded-md border border-dashed border-border',
        typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
      )}
    >
      <div
        role="tablist"
        aria-orientation="horizontal"
        className="flex flex-wrap items-end border-b border-border bg-muted"
      >
        {children.map((c, i) => {
          const hasErrors = interactive && subtreeHasErrors(c, errorByBindTo)
          const label = tabLabel(c, i)
          const isActive = i === safeIndex
          return (
            <button
              key={c.id}
              type="button"
              role="tab"
              id={tabId(c.id)}
              aria-selected={isActive}
              aria-controls={isActive ? activePanelIdAttr : undefined}
              onClick={() => setActiveTabId(c.id)}
              aria-label={hasErrors ? `${label} (has validation errors)` : label}
              className={cn(
                'flex shrink-0 items-center gap-1.5 whitespace-nowrap px-3.5 py-2.5 text-xs font-medium transition-colors',
                isActive
                  ? 'border-b-2 border-primary text-primary'
                  : 'text-muted-foreground hover:text-foreground',
              )}
            >
              <span>{label}</span>
              {hasErrors && (
                <span
                  className="inline-block h-1.5 w-1.5 rounded-full bg-destructive"
                  aria-hidden="true"
                />
              )}
            </button>
          )
        })}
      </div>
      <div
        style={{ padding: `${padding}px` }}
        role="tabpanel"
        id={activePanelIdAttr}
        aria-labelledby={activeTabIdAttr}
      >
        {activeChild && (
          <ElementRenderer element={activeChild} interactive={interactive} interactiveProps={interactiveProps} parentType="Tabs" />
        )}
      </div>
    </div>
  )
}

function RepeaterFieldLeaf({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  const fieldName = typeof p.fieldName === 'string' ? p.fieldName.trim() : ''
  const styleClasses = typeof p.styleClasses === 'string' ? p.styleClasses : ''
  const inlineStyleRaw = typeof p.inlineStyle === 'string' ? p.inlineStyle : ''
  const mapExpressionRaw = typeof p.mapExpression === 'string' ? p.mapExpression : ''
  // parseInlineStyle hand-walks the declaration string with paren/quote
  // tracking — expensive enough to warrant memoization across rows in a
  // Repeater table where N rows × M Field columns would otherwise re-parse
  // the same string on every parent render.
  const parsedInlineStyle = useMemo(() => parseInlineStyle(inlineStyleRaw), [inlineStyleRaw])
  // Compile the optional map expression once per (expression, fieldName) — same
  // memoization rationale as parseInlineStyle above (re-used across every row).
  const mapValue = useMemo(
    () => compileMapExpression(mapExpressionRaw, fieldName),
    [mapExpressionRaw, fieldName],
  )

  // A top-level Field marked "Show as table column" is a record-list column, not a
  // form field — render nothing in the live form. On the designer canvas
  // (interactive === false) it stays visible so the author can still select and
  // configure it. (Fields inside a Repeater never carry isTableColumn — the
  // inspector only offers it for top-level Fields.)
  if (interactive && p.isTableColumn === true) return null

  let display: string
  if (fieldName !== '' && interactiveProps) {
    // Apply the author's map expression to the field's own value before coercion;
    // compileMapExpression is identity when no expression is set and swallows errors.
    const raw = mapValue(interactiveProps.formData[fieldName])
    if (raw === undefined || raw === null) display = ''
    else if (typeof raw === 'boolean') display = raw ? 'Yes' : 'No'
    else if (typeof raw === 'object') {
      try {
        display = JSON.stringify(raw)
      } catch {
        display = String(raw)
      }
    } else {
      display = String(raw)
    }
  } else {
    display = fieldName === '' ? '{ field }' : `{${fieldName}}`
  }

  return (
    <span className={cn('inline-block text-sm text-foreground', styleClasses)} style={parsedInlineStyle}>
      {display === '' ? ' ' : display}
    </span>
  )
}

function LeafRenderer({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  // `bindTo` is the local variable name for the resolved form-state key.
  // Sourced from getFieldKey() so we prefer `properties.fieldKey` and fall
  // back to legacy `properties.bindTo` for older saved schemas.
  const bindTo = getFieldKey(p)

  switch (element.type) {
    case 'Label': {
      const rawFontSize = Number(p.fontSize ?? 14)
      const fontSize = isNaN(rawFontSize) ? 14 : rawFontSize
      const fontWeight = p.fontWeight === 'bold' ? 'bold' : 'normal'
      // Only emit inline fontSize/fontWeight when the author explicitly set
      // them away from the defaults (14 / normal). Inline style beats Tailwind
      // utilities, so emitting defaults would silently defeat any text-/font-*
      // class typed into Style classes.
      const inlineStyle: { fontSize?: string; fontWeight?: 'bold' | 'normal' } = {}
      if (fontSize !== 14) inlineStyle.fontSize = `${fontSize}px`
      if (fontWeight !== 'normal') inlineStyle.fontWeight = fontWeight
      return (
        <span
          className={cn(
            'block text-sm text-foreground',
            typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
          )}
          style={inlineStyle}
        >
          {String(p.text ?? 'Label')}
        </span>
      )
    }
    case 'Button': {
      const variant = p.variant === 'secondary'
        ? 'bg-secondary text-secondary-foreground'
        : p.variant === 'danger'
          ? 'bg-destructive text-destructive-foreground'
          : 'bg-primary text-primary-foreground'
      const isPrimary = p.variant !== 'secondary' && p.variant !== 'danger'
      const interactivePrimary = interactive && isPrimary
      const dimmed = interactivePrimary && interactiveProps?.formIsValid === false
      return (
        <button
          type="button"
          disabled={!interactivePrimary}
          onClick={interactivePrimary ? () => interactiveProps?.onPrimaryButtonClick() : undefined}
          className={cn(
            'rounded px-3 py-1.5 text-xs',
            variant,
            interactivePrimary ? '' : 'opacity-70 cursor-not-allowed',
            dimmed && 'opacity-60',
            typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
          )}
        >
          {String(p.label ?? 'Button')}
        </button>
      )
    }
    case 'Text Input': {
      if (interactive) {
        const bound = bindTo !== ''
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <LabeledField element={element} interactive={interactive}>
            <input
              id={inputIdFor(element, interactive)}
              type={resolveTextInputType(p)}
              placeholder={String(p.placeholder ?? '')}
              className={cn(
                LEAF_BASE,
                error && ERROR_INPUT_CLASS,
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              aria-label={ariaFallback(element, 'Text Input')}
              aria-invalid={error ? true : undefined}
              aria-describedby={errorId}
              value={bound ? String(interactiveProps?.formData[bindTo] ?? '') : undefined}
              onChange={bound ? (e) => interactiveProps?.onChange(bindTo, e.target.value) : undefined}
            />
            {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </LabeledField>
        )
      }
      const rowValue =
        bindTo !== '' && interactiveProps
          ? String(interactiveProps.formData[bindTo] ?? '')
          : undefined
      return (
        <LabeledField element={element} interactive={interactive}>
          <input
            id={inputIdFor(element, interactive)}
            type={resolveTextInputType(p)}
            disabled
            placeholder={String(p.placeholder ?? '')}
            value={rowValue}
            className={cn(
              LEAF_BASE,
              typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
            )}
            aria-label={ariaFallback(element, 'Text Input')}
          />
        </LabeledField>
      )
    }
    case 'TextArea': {
      const rowsRaw = Number(p.rows ?? 4)
      const rows = Number.isFinite(rowsRaw) && rowsRaw >= 1 ? Math.floor(rowsRaw) : 4
      if (interactive) {
        const bound = bindTo !== ''
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <LabeledField element={element} interactive={interactive}>
            <textarea
              id={inputIdFor(element, interactive)}
              placeholder={String(p.placeholder ?? '')}
              rows={rows}
              maxLength={typeof p.maxLength === 'number' && p.maxLength > 0 ? p.maxLength : undefined}
              className={cn(
                LEAF_BASE,
                // Textareas are multi-line: drop the single-line h-9 from LEAF_BASE
                // (tailwind-merge keeps this later h-auto) so `rows` drives height.
                'h-auto resize-y leading-snug',
                error && ERROR_INPUT_CLASS,
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              aria-label={ariaFallback(element, 'Text Area')}
              aria-invalid={error ? true : undefined}
              aria-describedby={errorId}
              value={bound ? String(interactiveProps?.formData[bindTo] ?? '') : undefined}
              onChange={bound ? (e) => interactiveProps?.onChange(bindTo, e.target.value) : undefined}
            />
            {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </LabeledField>
        )
      }
      const rowValue =
        bindTo !== '' && interactiveProps
          ? String(interactiveProps.formData[bindTo] ?? '')
          : undefined
      return (
        <LabeledField element={element} interactive={interactive}>
          <textarea
            id={inputIdFor(element, interactive)}
            disabled
            placeholder={String(p.placeholder ?? '')}
            rows={rows}
            value={rowValue}
            className={cn(
              LEAF_BASE,
              'h-auto resize-none leading-snug',
              typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
            )}
            aria-label={ariaFallback(element, 'Text Area')}
          />
        </LabeledField>
      )
    }
    case 'Number Input': {
      if (interactive) {
        const bound = bindTo !== ''
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <LabeledField element={element} interactive={interactive}>
            <input
              id={inputIdFor(element, interactive)}
              type="number"
              placeholder={String(p.label ?? '')}
              className={cn(
                LEAF_BASE,
                error && ERROR_INPUT_CLASS,
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              aria-label={ariaFallback(element, 'Number Input')}
              aria-invalid={error ? true : undefined}
              aria-describedby={errorId}
              value={bound ? String(interactiveProps?.formData[bindTo] ?? '') : undefined}
              onChange={
                bound
                  ? (e) => {
                    const n = e.target.valueAsNumber
                    interactiveProps?.onChange(bindTo, isNaN(n) ? undefined : n)
                  }
                  : undefined
              }
            />
            {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </LabeledField>
        )
      }
      const rowValue =
        bindTo !== '' && interactiveProps
          ? String(interactiveProps.formData[bindTo] ?? '')
          : undefined
      return (
        <LabeledField element={element} interactive={interactive}>
          <input
            id={inputIdFor(element, interactive)}
            type="number"
            disabled
            placeholder={String(p.label ?? '')}
            value={rowValue}
            className={cn(
              LEAF_BASE,
              typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
            )}
            aria-label={ariaFallback(element, 'Number Input')}
          />
        </LabeledField>
      )
    }
    case 'Checkbox': {
      const dc = p.defaultChecked === true || p.defaultChecked === 'true'
      if (interactive) {
        const bound = bindTo !== ''
        const stored = bound ? interactiveProps?.formData[bindTo] : undefined
        const checked = stored === undefined ? dc : !!stored
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <div className="flex flex-col">
            <label
              className={cn(
                'flex cursor-pointer items-center gap-2 text-xs text-muted-foreground',
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
            >
              <input
                type="checkbox"
                checked={checked}
                onChange={bound ? (e) => interactiveProps?.onChange(bindTo, e.target.checked) : undefined}
                aria-invalid={error ? true : undefined}
                aria-describedby={errorId}
                className={cn('h-3.5 w-3.5', error && ERROR_INPUT_CLASS)}
              />
              {String(p.label ?? 'Checkbox')}
            </label>
            {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </div>
        )
      }
      const rowChecked =
        bindTo !== '' && interactiveProps
          ? !!interactiveProps.formData[bindTo]
          : dc
      return (
        <label
          className={cn(
            'flex cursor-not-allowed items-center gap-2 text-xs text-muted-foreground',
            typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
          )}
        >
          <input
            type="checkbox"
            disabled
            checked={rowChecked}
            className="h-3.5 w-3.5"
          />
          {String(p.label ?? 'Checkbox')}
        </label>
      )
    }
    case 'Dropdown': {
      const isDesigner = p.optionsSource === 'designer'
      const isApi = p.optionsSource === 'api'
      const isDataset = p.optionsSource === 'dataset'
      if (interactive) {
        if (isDesigner)
          return <DesignerDropdownField element={element} interactiveProps={interactiveProps} />
        if (isDataset)
          return <DatasetDropdownField element={element} interactiveProps={interactiveProps} />
        if (isApi)
          return <ApiDropdownField element={element} interactiveProps={interactiveProps} />
        return <StaticDropdownField element={element} interactiveProps={interactiveProps} />
      }
      // Preview (non-interactive): a disabled box showing the current value.
      const rowValue =
        bindTo !== '' && interactiveProps
          ? String(interactiveProps.formData[bindTo] ?? '')
          : ''
      const optsList =
        isDesigner || isApi || isDataset
          ? []
          : String(p.options ?? '')
              .split(',')
              .map((o) => o.trim())
              .filter(Boolean)
      return (
        <LabeledField element={element} interactive={interactive}>
          <select
            id={inputIdFor(element, interactive)}
            disabled
            value={rowValue}
            className={cn(
              LEAF_BASE,
              typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
            )}
            aria-label={ariaFallback(element, 'Dropdown')}
          >
            {rowValue !== '' && !optsList.includes(rowValue) && (
              <option value={rowValue}>{rowValue}</option>
            )}
            {rowValue === '' && <option value="">{String(p.placeholder ?? 'Select…')}</option>}
            {optsList.map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
          {isDesigner && (
            <p className="mt-1 text-[10px] text-muted-foreground">(designer lookup — preview unavailable)</p>
          )}
          {isApi && (
            <p className="mt-1 text-[10px] text-muted-foreground">(API lookup — preview unavailable)</p>
          )}
        </LabeledField>
      )
    }
    case 'DateTime Picker': {
      const inputType = p.format === 'datetime' ? 'datetime-local' : 'date'
      if (interactive) {
        const bound = bindTo !== ''
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <LabeledField element={element} interactive={interactive}>
            <input
              id={inputIdFor(element, interactive)}
              type={inputType}
              className={cn(
                LEAF_BASE,
                error && ERROR_INPUT_CLASS,
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              aria-label={ariaFallback(element, 'DateTime')}
              aria-invalid={error ? true : undefined}
              aria-describedby={errorId}
              value={bound ? String(interactiveProps?.formData[bindTo] ?? '') : undefined}
              onChange={bound ? (e) => interactiveProps?.onChange(bindTo, e.target.value) : undefined}
            />
            {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </LabeledField>
        )
      }
      const rowValue =
        bindTo !== '' && interactiveProps
          ? String(interactiveProps.formData[bindTo] ?? '')
          : undefined
      return (
        <LabeledField element={element} interactive={interactive}>
          <input
            id={inputIdFor(element, interactive)}
            type={inputType}
            disabled
            value={rowValue}
            className={cn(
              LEAF_BASE,
              typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
            )}
            aria-label={ariaFallback(element, 'DateTime')}
          />
        </LabeledField>
      )
    }
    case 'Color Picker': {
      if (interactive) {
        const bound = bindTo !== ''
        const fallbackRaw = String(p.defaultColor ?? '#000000')
        const fallback = HEX_COLOR.test(fallbackRaw) ? fallbackRaw : '#000000'
        const stored = bound ? interactiveProps?.formData[bindTo] : undefined
        const value =
          typeof stored === 'string' && HEX_COLOR.test(stored) ? stored : fallback
        const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
        const errorId = error ? `${element.id}-error` : undefined
        return (
          <LabeledField element={element} interactive={interactive}>
            <input
              id={inputIdFor(element, interactive)}
              type="color"
              value={value}
              onChange={bound ? (e) => interactiveProps?.onChange(bindTo, e.target.value) : undefined}
              className={cn(
                'h-7 w-7 cursor-pointer rounded border border-border',
                error && ERROR_INPUT_CLASS,
                typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
              )}
              aria-label={ariaFallback(element, 'Color')}
              aria-invalid={error ? true : undefined}
              aria-describedby={errorId}
            />
            {error && <p id={errorId} role="alert" className={ERROR_MESSAGE_CLASS}>{error}</p>}
          </LabeledField>
        )
      }
      // Read-only swatch — accepts any CSS color string (#RRGGBB, #fff, rgb()/rgba(),
      // named colors). An <input type="color"> would only display 6-digit hex and
      // silently substitute defaults for anything else.
      const rowRaw =
        bindTo !== '' && interactiveProps ? interactiveProps.formData[bindTo] : undefined
      const rowValue =
        typeof rowRaw === 'string' && rowRaw.trim() !== ''
          ? rowRaw
          : String(p.defaultColor ?? '#000000')
      return (
        <LabeledField element={element} interactive={interactive}>
          <Tooltip>
            <TooltipTrigger asChild>
              <span
                id={inputIdFor(element, interactive)}
                role="img"
                aria-label={ariaFallback(element, 'Color') ?? `Color ${rowValue}`}
                className={cn(
                  'inline-block h-7 w-7 rounded border border-border',
                  typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
                )}
                style={{ background: rowValue }}
              />
            </TooltipTrigger>
            <TooltipContent>{rowValue}</TooltipContent>
          </Tooltip>
        </LabeledField>
      )
    }
    case 'Repeater Field':
      return <RepeaterFieldLeaf element={element} interactive={interactive} interactiveProps={interactiveProps} />
    case 'File':
      return (
        <FileFieldRenderer
          element={element}
          interactive={interactive}
          interactiveProps={interactiveProps}
        />
      )
    case 'Image': {
      return <ImageRenderer element={element} />
    }
    case 'RawHtml': {
      // Author-supplied markup injected verbatim. This is intentional raw-HTML
      // rendering — the designer schema is authored by trusted internal users,
      // the same trust boundary the Repeater Field inline-style and inspector
      // help text (also dangerouslySetInnerHTML) already rely on. Renders the
      // same in canvas (non-interactive) and runtime (DynamicComponent) modes
      // since it binds to no form state.
      const html = typeof p.rawHtml === 'string' ? p.rawHtml : ''
      if (html.trim() === '') {
        return (
          <div className="rounded-lg border border-dashed border-field-border px-3 py-2 text-sm text-placeholder">
            {'<RawHtml>'}
          </div>
        )
      }
      return (
        <div
          className={cn(typeof p.styleClasses === 'string' ? p.styleClasses : undefined)}
          dangerouslySetInnerHTML={{ __html: html }}
        />
      )
    }
    default:
      return <div className={LEAF_BASE}>{element.type}</div>
  }
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

const IMAGE_EXT_RE = /\.(jpg|jpeg|png|gif|webp|svg)$/i

// Renders a File upload field. In non-interactive (canvas preview) mode it shows
// a dropzone placeholder. In interactive mode it lets the user pick a file; the
// File object is written into formData[bindTo] and uploaded to MinIO when the
// form is saved (DynamicComponent.handlePrimaryButtonClick resolves it).
// Existing values (string object keys from the DB) are displayed as an image
// thumbnail (via presigned URL) for image types, or as a filename chip otherwise.
function FileFieldRenderer({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  const { t } = useTranslation()
  const bindTo = getFieldKey(element.properties)
  const accept = typeof element.properties.accept === 'string' ? element.properties.accept : undefined
  const qc = useQueryClient()
  const inputRef = useRef<HTMLInputElement>(null)
  const uid = useId()

  const raw = bindTo !== '' ? interactiveProps?.formData[bindTo] : undefined
  const pendingFile = raw instanceof File ? raw : null
  const existingKey = typeof raw === 'string' && raw !== '' ? raw : null

  const isImageKey = existingKey ? IMAGE_EXT_RE.test(existingKey) : false
  const isPendingImage = pendingFile ? pendingFile.type.startsWith('image/') : false

  // Presigned URL for existing stored key (only fetched for image types)
  const { data: presignData } = useQuery({
    queryKey: ['files-presign', existingKey],
    queryFn: () => filesApi.getPresignedUrl(existingKey!),
    enabled: !!existingKey && isImageKey,
    staleTime: 50 * 60 * 1000,
  })

  // Object URL for a locally-selected image (freed on cleanup / file change)
  const [pendingPreviewUrl, setPendingPreviewUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!pendingFile || !isPendingImage) {
      setPendingPreviewUrl(null)
      return
    }
    const url = URL.createObjectURL(pendingFile)
    setPendingPreviewUrl(url)
    return () => URL.revokeObjectURL(url)
  }, [pendingFile, isPendingImage])

  const hasFile = !!(pendingFile ?? existingKey)

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file && bindTo !== '') interactiveProps?.onChange(bindTo, file)
    e.target.value = '' // allow re-selecting the same file
  }

  const handleRemove = () => {
    if (bindTo !== '') interactiveProps?.onChange(bindTo, undefined)
    if (existingKey) void qc.invalidateQueries({ queryKey: ['files-presign', existingKey] })
  }

  // ── canvas preview (non-interactive) ──────────────────────────────────────
  if (!interactive) {
    return (
      <LabeledField element={element} interactive={false}>
        <div className="flex h-24 flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed border-border bg-field text-muted-foreground">
          <Paperclip className="h-5 w-5" />
          <span className="text-xs">{t('designer.renderer.fileEmpty')}</span>
          {accept && <span className="text-[10px] opacity-60">{accept}</span>}
        </div>
      </LabeledField>
    )
  }

  // ── interactive ───────────────────────────────────────────────────────────
  return (
    <LabeledField element={element} interactive={true}>
      <input
        ref={inputRef}
        id={uid}
        type="file"
        accept={accept}
        className="sr-only"
        onChange={handleInputChange}
        aria-label={String(element.properties.label ?? 'File')}
      />

      {!hasFile ? (
        // Empty dropzone
        <button
          type="button"
          onClick={() => inputRef.current?.click()}
          className={cn(
            'flex h-24 w-full flex-col items-center justify-center gap-2 rounded-lg',
            'border-2 border-dashed border-border bg-field text-muted-foreground',
            'transition-colors hover:border-primary hover:text-primary',
          )}
        >
          <Upload className="h-5 w-5" />
          <span className="text-xs">{t('designer.renderer.fileClickToUpload')}</span>
          {accept && <span className="text-[10px] opacity-60">{accept}</span>}
        </button>
      ) : (
        <div className="flex flex-col gap-2">
          {/* Image preview */}
          {(isPendingImage || isImageKey) && (
            <div className="overflow-hidden rounded-lg border border-border bg-muted">
              <img
                src={isPendingImage ? (pendingPreviewUrl ?? '') : (presignData?.url ?? '')}
                alt=""
                className="max-h-48 w-full object-contain"
                onError={(e) => {
                  ;(e.currentTarget as HTMLImageElement).style.display = 'none'
                }}
              />
            </div>
          )}
          {/* Filename chip with change / remove actions */}
          <div className="flex items-center gap-2 rounded-lg border border-border bg-field px-3 py-2">
            <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />
            <span className="flex-1 truncate text-sm text-foreground">
              {pendingFile
                ? pendingFile.name
                : (existingKey?.split('/').pop() ?? existingKey ?? '')}
            </span>
            {pendingFile && (
              <span className="text-[10px] text-muted-foreground">
                {formatFileSize(pendingFile.size)}
              </span>
            )}
            {pendingFile && (
              <span className="rounded bg-muted px-1.5 py-0.5 text-[10px] text-muted-foreground">
                {t('designer.renderer.filePendingHint')}
              </span>
            )}
            <div className="flex shrink-0 items-center gap-1">
              <button
                type="button"
                title={t('designer.renderer.fileChange')}
                onClick={() => inputRef.current?.click()}
                className="rounded p-1 text-muted-foreground transition-colors hover:text-foreground"
              >
                <Pencil className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                title={t('designer.renderer.fileRemove')}
                onClick={handleRemove}
                className="rounded p-1 text-muted-foreground transition-colors hover:text-destructive"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          </div>
        </div>
      )}
    </LabeledField>
  )
}

// Renders an Image leaf whose `src` property is interpreted as a backend object
// key. The component calls the files-presign endpoint to swap the key for a
// short-lived URL before passing it to <img>. Absolute URLs (http(s):, data:,
// blob:) bypass presigning so inline samples keep working.
const ABSOLUTE_URL_RE = /^(https?:\/\/|data:|blob:)/
// Upper bound on width/height to prevent a malformed schema (`width: 999999`)
// from blowing up layout.
const MAX_IMAGE_DIMENSION_PX = 4000

function clampDimension(raw: unknown): number | undefined {
  const n = raw !== undefined ? Number(raw) : NaN
  if (!Number.isFinite(n) || n <= 0) return undefined
  return Math.min(n, MAX_IMAGE_DIMENSION_PX)
}

function ImageRenderer({ element }: { element: DesignerElement }) {
  const { t } = useTranslation()
  const p = element.properties
  const rawSrc = String(p.src ?? '').trim()
  const isLikelyAbsoluteUrl = ABSOLUTE_URL_RE.test(rawSrc)
  const width = clampDimension(p.width)
  const height = clampDimension(p.height)
  const alt = String(p.alt ?? '')
  const qc = useQueryClient()

  // Track which rawSrc value last emitted <img onError>. Stored as the value
  // itself rather than a boolean so that toggling rawSrc from "broken" → "good"
  // automatically resolves to "not failed" without an effect to clear the latch.
  const [failedForSrc, setFailedForSrc] = useState<string | null>(null)
  const imgFailed = failedForSrc === rawSrc

  // FormForge files API stub returns `{ url }`. staleTime caches the URL for
  // ~50 minutes (typical 1-hour presign window). On <img onError> we invalidate
  // the presign so a transient blip or post-expiry failure refetches a fresh
  // signed URL instead of latching to the fallback UI for the rest of the
  // session.
  const { data, isLoading, isError } = useQuery({
    queryKey: ['files-presign', rawSrc],
    queryFn: () => filesApi.getPresignedUrl(rawSrc),
    enabled: rawSrc !== '' && !isLikelyAbsoluteUrl,
    staleTime: 50 * 60 * 1000,
  })

  // Clear the latch when the presign refetch returns a NEW signed URL — the
  // refetch is the recovery path; without this the latch would still equal
  // rawSrc and the fallback UI would persist past the successful refresh.
  const lastResolvedUrlRef = useRef<string | null | undefined>(undefined)
  useEffect(() => {
    if (data?.url && data.url !== lastResolvedUrlRef.current) {
      lastResolvedUrlRef.current = data.url
      setFailedForSrc(null)
    }
  }, [data?.url])

  if (rawSrc === '') {
    return (
      <div className="flex h-16 items-center justify-center rounded border border-dashed border-border text-xs text-muted-foreground">
        {t('designer.renderer.imageEmptyPlaceholder')}
      </div>
    )
  }

  if (!isLikelyAbsoluteUrl && isLoading) {
    return (
      <div
        className="h-32 w-full animate-pulse rounded bg-muted"
        style={{
          width: width !== undefined ? `${width}px` : undefined,
          height: height !== undefined ? `${height}px` : undefined,
        }}
        aria-busy="true"
      />
    )
  }

  const resolvedSrc = isLikelyAbsoluteUrl ? rawSrc : (data?.url ?? null)
  if (isError || imgFailed || resolvedSrc === null) {
    return (
      <div
        className="flex h-32 w-full flex-col items-center justify-center gap-1 rounded border border-dashed border-border bg-muted text-xs text-muted-foreground"
        style={{
          width: width !== undefined ? `${width}px` : undefined,
          height: height !== undefined ? `${height}px` : undefined,
        }}
      >
        <ImageOff className="h-6 w-6 text-muted-foreground/50" />
        <span>{t('designer.renderer.imageUnavailable')}</span>
      </div>
    )
  }

  return (
    <img
      src={resolvedSrc}
      alt={alt}
      width={width}
      height={height}
      onError={() => {
        setFailedForSrc(rawSrc)
        if (!isLikelyAbsoluteUrl) {
          void qc.invalidateQueries({ queryKey: ['files-presign', rawSrc] })
        }
      }}
      className={cn(
        'max-h-32 max-w-full rounded border border-border object-contain',
        typeof p.styleClasses === 'string' ? p.styleClasses : undefined,
      )}
    />
  )
}

// Static dropdown (optionsSource = 'static'): a searchable combobox over the
// author's comma-separated option list, filtered client-side.
function StaticDropdownField({
  element,
  interactiveProps,
}: {
  element: DesignerElement
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  const bindTo = getFieldKey(p)
  const bound = bindTo !== ''
  const onChange = interactiveProps?.onChange
  const value = bound ? String(interactiveProps?.formData[bindTo] ?? '') : ''
  const options = useMemo<ComboboxOption[]>(
    () =>
      String(p.options ?? '')
        .split(',')
        .map((o) => o.trim())
        .filter(Boolean)
        .map((o) => ({ value: o, label: o })),
    [p.options],
  )
  const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
  const errorId = error ? `${element.id}-error` : undefined
  const styleClasses = typeof p.styleClasses === 'string' ? p.styleClasses : undefined

  return (
    <LabeledField element={element} interactive={true}>
      <Combobox
        id={inputIdFor(element, true)}
        value={value}
        onValueChange={(v) => onChange?.(bindTo, v)}
        options={options}
        placeholder={p.placeholder ? String(p.placeholder) : 'Select…'}
        disabled={!bound}
        className={cn(error && ERROR_INPUT_CLASS, styleClasses)}
        aria-label={ariaFallback(element, 'Dropdown')}
        aria-invalid={error ? true : undefined}
        aria-describedby={errorId}
      />
      {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
    </LabeledField>
  )
}

// Designer-backed dropdown (optionsSource = 'designer'): a searchable combobox
// whose options are fetched (server-searched + paginated) from another
// designer's table via /api/data/{id}/options. `dependsOn` (local:target pairs)
// cascades by filtering the target columns by the form's local field values.
function DesignerDropdownField({
  element,
  interactiveProps,
}: {
  element: DesignerElement
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  const bindTo = getFieldKey(p)
  const bound = bindTo !== ''
  const onChange = interactiveProps?.onChange
  const formData = interactiveProps?.formData

  const designerId = String(p.optionsDesignerId ?? '').trim()
  const version = toRowVersion(p.optionsVersion)
  const labelField = String(p.labelField ?? '').trim()
  const valueField = String(p.valueField ?? '').trim()
  const configured =
    designerId !== '' && version !== undefined && labelField !== '' && valueField !== ''

  // Cascading map; drop a self-reference so a user pick can't trigger its own reset.
  const depMap = useMemo(
    () => parseDependsOnMap(p.dependsOn).filter((d) => d.local !== bindTo),
    [p.dependsOn, bindTo],
  )
  // Cheap to recompute each render for the small dependency lists we expect;
  // its string VALUE is what downstream memo/effect deps key on.
  const depSignature = JSON.stringify(depMap.map((d) => formData?.[d.local] ?? null))
  const depsReady = depMap.every((d) => {
    const v = formData?.[d.local]
    return v !== undefined && v !== null && v !== ''
  })
  const filter = useMemo(() => {
    const f: Record<string, string> = {}
    for (const d of depMap) {
      const v = formData?.[d.local]
      if (v !== undefined && v !== null && v !== '') f[d.target] = String(v)
    }
    return f
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [depSignature])

  // Debounce the search box so we issue one request per pause, not per keystroke.
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(search), 250)
    return () => window.clearTimeout(id)
  }, [search])

  const enabled = configured && depsReady
  const filterKey = JSON.stringify(filter)

  const optionsQuery = useQuery({
    queryKey: ['dropdown-options', designerId, version, labelField, valueField, filterKey, debouncedSearch],
    queryFn: () =>
      fetchDropdownOptions(designerId, {
        version: version!,
        labelField,
        valueField,
        search: debouncedSearch || undefined,
        page: 1,
        pageSize: 50,
        filter,
      }),
    enabled,
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  })
  const pageOptions = useMemo<ComboboxOption[]>(
    () => (optionsQuery.data?.data ?? []).map((o) => ({ value: o.value, label: o.label ?? o.value })),
    [optionsQuery.data],
  )

  const value = bound ? String(formData?.[bindTo] ?? '') : ''
  const selectedInPage = value !== '' && pageOptions.some((o) => o.value === value)

  // Resolve the label for a selected value that isn't on the current page (e.g.
  // editing a saved record whose row isn't in the first 50 search results).
  const selectedQuery = useQuery({
    queryKey: ['dropdown-option-label', designerId, version, labelField, valueField, value],
    queryFn: () =>
      fetchDropdownOptions(designerId, {
        version: version!,
        labelField,
        valueField,
        page: 1,
        pageSize: 1,
        filter: { [valueField]: value },
      }),
    enabled: configured && value !== '' && !selectedInPage,
    staleTime: 60_000,
  })
  const selectedLabel = selectedQuery.data?.data?.[0]?.label
  const options =
    value !== '' && !selectedInPage && selectedLabel
      ? [{ value, label: selectedLabel }, ...pageOptions]
      : pageOptions

  // Cascade reset: when a parent value changes (ready→ready, different), the
  // saved value may no longer be valid — clear it. Skip first render and
  // not-ready→ready hydration.
  const firstRenderRef = useRef(true)
  const prevSigRef = useRef(depSignature)
  const prevReadyRef = useRef(depsReady)
  useEffect(() => {
    const wasReady = prevReadyRef.current
    const prevSig = prevSigRef.current
    prevReadyRef.current = depsReady
    prevSigRef.current = depSignature
    if (firstRenderRef.current) {
      firstRenderRef.current = false
      return
    }
    if (!wasReady || !depsReady) return
    if (prevSig === depSignature) return
    if (value === '') return
    if (bound && depMap.length > 0 && onChange) onChange(bindTo, '')
  }, [depSignature, depsReady, value, bound, bindTo, depMap.length, onChange])

  const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
  const errorId = error ? `${element.id}-error` : undefined
  const styleClasses = typeof p.styleClasses === 'string' ? p.styleClasses : undefined

  const placeholder = !configured
    ? 'Not configured'
    : !depsReady
      ? `Select ${depMap.map((d) => d.local).join(', ')} first`
      : p.placeholder
        ? String(p.placeholder)
        : 'Select…'

  return (
    <LabeledField element={element} interactive={true}>
      <Combobox
        id={inputIdFor(element, true)}
        value={value}
        onValueChange={(v) => onChange?.(bindTo, v)}
        options={options}
        onSearchChange={setSearch}
        loading={optionsQuery.isLoading}
        disabled={!bound || !enabled}
        placeholder={placeholder}
        emptyText={optionsQuery.isError ? 'Failed to load' : 'No results'}
        className={cn(error && ERROR_INPUT_CLASS, styleClasses)}
        aria-label={ariaFallback(element, 'Dropdown')}
        aria-invalid={error ? true : undefined}
        aria-describedby={errorId}
      />
      {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
    </LabeledField>
  )
}

// Dataset-backed dropdown (optionsSource = 'dataset'): a searchable combobox whose
// options are fetched (server-searched + paginated) from a Dataset Manager dataset's
// backing VIEW via /api/datasets/by-name/{name}/options. optionsDatasetId holds the
// dataset's VIEW NAME (not a GUID), mirroring how a Designer-backed dropdown stores a
// table name. labelField/valueField name columns of that VIEW. `dependsOn`
// (local:target pairs) cascades by filtering the target VIEW columns by the form's
// local field values — identical semantics to the Designer-backed source.
function DatasetDropdownField({
  element,
  interactiveProps,
}: {
  element: DesignerElement
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  const bindTo = getFieldKey(p)
  const bound = bindTo !== ''
  const onChange = interactiveProps?.onChange
  const formData = interactiveProps?.formData

  const datasetName = String(p.optionsDatasetId ?? '').trim()
  const labelField = String(p.labelField ?? '').trim()
  const valueField = String(p.valueField ?? '').trim()
  const configured = datasetName !== '' && labelField !== '' && valueField !== ''

  // Cascading map; drop a self-reference so a user pick can't trigger its own reset.
  const depMap = useMemo(
    () => parseDependsOnMap(p.dependsOn).filter((d) => d.local !== bindTo),
    [p.dependsOn, bindTo],
  )
  // Cheap to recompute each render for the small dependency lists we expect;
  // its string VALUE is what downstream memo/effect deps key on.
  const depSignature = JSON.stringify(depMap.map((d) => formData?.[d.local] ?? null))
  const depsReady = depMap.every((d) => {
    const v = formData?.[d.local]
    return v !== undefined && v !== null && v !== ''
  })
  const filter = useMemo(() => {
    const f: Record<string, string> = {}
    for (const d of depMap) {
      const v = formData?.[d.local]
      if (v !== undefined && v !== null && v !== '') f[d.target] = String(v)
    }
    return f
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [depSignature])

  // Debounce the search box so we issue one request per pause, not per keystroke.
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(search), 250)
    return () => window.clearTimeout(id)
  }, [search])

  const enabled = configured && depsReady
  const filterKey = JSON.stringify(filter)

  const optionsQuery = useQuery({
    queryKey: ['dataset-dropdown-options', datasetName, labelField, valueField, filterKey, debouncedSearch],
    queryFn: () =>
      fetchDatasetOptions(datasetName, {
        labelField,
        valueField,
        search: debouncedSearch || undefined,
        page: 1,
        pageSize: 50,
        filter,
      }),
    enabled,
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  })
  const pageOptions = useMemo<ComboboxOption[]>(
    () => (optionsQuery.data?.data ?? []).map((o) => ({ value: o.value, label: o.label ?? o.value })),
    [optionsQuery.data],
  )

  const value = bound ? String(formData?.[bindTo] ?? '') : ''
  const selectedInPage = value !== '' && pageOptions.some((o) => o.value === value)

  // Resolve the label for a selected value that isn't on the current page (e.g.
  // editing a saved record whose row isn't in the first 50 search results).
  const selectedQuery = useQuery({
    queryKey: ['dataset-dropdown-option-label', datasetName, labelField, valueField, value],
    queryFn: () =>
      fetchDatasetOptions(datasetName, {
        labelField,
        valueField,
        value,
        page: 1,
        pageSize: 1,
      }),
    enabled: configured && value !== '' && !selectedInPage,
    staleTime: 60_000,
  })
  const selectedLabel = selectedQuery.data?.data?.[0]?.label
  const options =
    value !== '' && !selectedInPage && selectedLabel
      ? [{ value, label: selectedLabel }, ...pageOptions]
      : pageOptions

  // Cascade reset: when a parent value changes (ready→ready, different), the
  // saved value may no longer be valid — clear it. Skip first render and
  // not-ready→ready hydration.
  const firstRenderRef = useRef(true)
  const prevSigRef = useRef(depSignature)
  const prevReadyRef = useRef(depsReady)
  useEffect(() => {
    const wasReady = prevReadyRef.current
    const prevSig = prevSigRef.current
    prevReadyRef.current = depsReady
    prevSigRef.current = depSignature
    if (firstRenderRef.current) {
      firstRenderRef.current = false
      return
    }
    if (!wasReady || !depsReady) return
    if (prevSig === depSignature) return
    if (value === '') return
    if (bound && depMap.length > 0 && onChange) onChange(bindTo, '')
  }, [depSignature, depsReady, value, bound, bindTo, depMap.length, onChange])

  const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
  const errorId = error ? `${element.id}-error` : undefined
  const styleClasses = typeof p.styleClasses === 'string' ? p.styleClasses : undefined

  const placeholder = !configured
    ? 'Not configured'
    : !depsReady
      ? `Select ${depMap.map((d) => d.local).join(', ')} first`
      : p.placeholder
        ? String(p.placeholder)
        : 'Select…'

  return (
    <LabeledField element={element} interactive={true}>
      <Combobox
        id={inputIdFor(element, true)}
        value={value}
        onValueChange={(v) => onChange?.(bindTo, v)}
        options={options}
        onSearchChange={setSearch}
        loading={optionsQuery.isLoading}
        disabled={!bound || !enabled}
        placeholder={placeholder}
        emptyText={optionsQuery.isError ? 'Failed to load' : 'No results'}
        className={cn(error && ERROR_INPUT_CLASS, styleClasses)}
        aria-label={ariaFallback(element, 'Dropdown')}
        aria-invalid={error ? true : undefined}
        aria-describedby={errorId}
      />
      {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
    </LabeledField>
  )
}

// API-endpoint dropdown (optionsSource = 'api'): a searchable combobox whose
// options are fetched at runtime from a relative platform endpoint (`apiPath`).
// `apiPath` may template `{fieldKey}` segments from the form's data, and
// `dependsOn` (comma-separated field keys) gates the fetch until those fields
// have values. The response is coerced into {label,value} pairs by the configured
// label/value fields. Search filters client-side over the fetched list.
function ApiDropdownField({
  element,
  interactiveProps,
}: {
  element: DesignerElement
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  const bindTo = getFieldKey(p)
  const bound = bindTo !== ''
  const onChange = interactiveProps?.onChange
  const formData = interactiveProps?.formData

  const apiPath = String(p.apiPath ?? '')
  const labelField = String(p.labelField ?? '')
  const valueField = String(p.valueField ?? '')

  // Filter `bindTo` out of `dependsOn`: a self-reference would create a
  // write-loop where every user selection triggers the cascade-reset effect
  // back to '' on the very key the user just set.
  const dependsOn = useMemo(
    () => parseDependsOn(p.dependsOn).filter((k) => k !== bindTo),
    [p.dependsOn, bindTo],
  )

  const allDependenciesReady = dependsOn.every((k) => {
    const v = formData?.[k]
    return v !== undefined && v !== null && v !== ''
  })

  const resolvedPath =
    allDependenciesReady && formData ? resolveApiPath(apiPath, formData) : null

  // Cheap to recompute each render for the small dependency lists we expect; its
  // string VALUE is what the cascade-reset effect keys on (not the array identity).
  const dependencySignature = JSON.stringify(dependsOn.map((k) => formData?.[k] ?? null))

  const optionsQuery = useQuery({
    // queryKey omits element.id so two dropdowns sharing a resolvedPath share a
    // single fetch via TanStack Query's natural dedup.
    queryKey: ['dynamic-dropdown-options', resolvedPath],
    queryFn: () => httpClient.get<unknown>(resolvedPath as string),
    staleTime: 60_000,
    enabled: resolvedPath !== null,
  })

  const options = useMemo<ComboboxOption[]>(
    () =>
      extractOptions(optionsQuery.data, labelField, valueField).map((o) => ({
        value: o.value,
        label: o.label,
      })),
    [optionsQuery.data, labelField, valueField],
  )

  const value = bound ? String(formData?.[bindTo] ?? '') : ''

  // Cascade reset: when a parent's value changes from one ready state to a
  // different ready state, the submitted value may no longer exist in the new
  // option list. Reset to ''. Skip not-ready→ready hydration and first render.
  const firstRenderRef = useRef(true)
  const prevSigRef = useRef(dependencySignature)
  const prevReadyRef = useRef(allDependenciesReady)
  useEffect(() => {
    const wasReady = prevReadyRef.current
    const prevSig = prevSigRef.current
    prevReadyRef.current = allDependenciesReady
    prevSigRef.current = dependencySignature
    if (firstRenderRef.current) {
      firstRenderRef.current = false
      return
    }
    if (!wasReady || !allDependenciesReady) return
    if (prevSig === dependencySignature) return
    if (value === '') return
    if (bound && dependsOn.length > 0 && onChange) onChange(bindTo, '')
  }, [dependencySignature, allDependenciesReady, value, bound, bindTo, dependsOn.length, onChange])

  const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
  const errorId = error ? `${element.id}-error` : undefined
  const styleClasses = typeof p.styleClasses === 'string' ? p.styleClasses : undefined

  const placeholder = !allDependenciesReady
    ? `Select ${dependsOn.join(', ')} first`
    : p.placeholder
      ? String(p.placeholder)
      : 'Select…'

  return (
    <LabeledField element={element} interactive={true}>
      <Combobox
        id={inputIdFor(element, true)}
        value={value}
        onValueChange={(v) => onChange?.(bindTo, v)}
        options={options}
        loading={optionsQuery.isLoading}
        disabled={!bound || !allDependenciesReady}
        placeholder={placeholder}
        emptyText={optionsQuery.isError ? 'Failed to load' : 'No results'}
        className={cn(error && ERROR_INPUT_CLASS, styleClasses)}
        aria-label={ariaFallback(element, 'Dropdown')}
        aria-invalid={error ? true : undefined}
        aria-describedby={errorId}
      />
      {error && <p id={errorId} className={ERROR_MESSAGE_CLASS}>{error}</p>}
    </LabeledField>
  )
}

function toFiniteNumber(raw: unknown): number | undefined {
  if (typeof raw === 'number' && Number.isFinite(raw)) return raw
  if (typeof raw === 'string' && raw.trim() !== '') {
    const n = Number(raw)
    if (Number.isFinite(n)) return n
  }
  return undefined
}

function toRowVersion(raw: unknown): number | undefined {
  const n = toFiniteNumber(raw)
  if (n === undefined) return undefined
  if (!Number.isInteger(n) || n <= 0) return undefined
  return n
}

// Walks the row template subtree in document order and returns every Repeater
// Field descendant. Each one becomes a column in the Repeater's tabular view.
function collectRepeaterFields(
  nodes: DesignerElement[],
  out: DesignerElement[] = [],
): DesignerElement[] {
  for (const node of nodes) {
    if (node.type === 'Repeater Field') {
      out.push(node)
      continue
    }
    if (node.children && node.children.length > 0) {
      collectRepeaterFields(node.children, out)
    }
  }
  return out
}

function headerLabelOf(field: DesignerElement): string {
  const rawHeader = field.properties.headerName
  if (typeof rawHeader === 'string' && rawHeader.trim() !== '') return rawHeader.trim()
  const rawField = field.properties.fieldName
  if (typeof rawField === 'string' && rawField.trim() !== '') return rawField.trim()
  return '—'
}

function RepeaterRenderer({
  element,
  interactive,
  interactiveProps,
}: {
  element: DesignerElement
  interactive: boolean
  interactiveProps?: InteractiveFormProps
}) {
  const p = element.properties
  // getFieldKey() — fieldKey-first, legacy bindTo fallback. See note above.
  const bindTo = getFieldKey(p)
  const label = String(p.label ?? '').trim()
  const rowDesignerId = String(p.rowDesignerId ?? '').trim()
  const rowVersion = toRowVersion(p.rowVersion)
  const minRows = toFiniteNumber(p.minRows)
  const maxRows = toFiniteNumber(p.maxRows)
  const children = element.children ?? []
  const showHeaders = p.showHeaders === true

  const bound = bindTo !== ''
  const rawList = bound ? interactiveProps?.formData[bindTo] : undefined
  // Defensive: older saved data may have a non-array for this key. Within an
  // array, drop any element that isn't a plain object — primitives/arrays
  // would crash the row-template render path on `formData[fieldName]`.
  const list: Record<string, unknown>[] = Array.isArray(rawList)
    ? (rawList.filter(isPlainObject) as Record<string, unknown>[])
    : []

  if (!interactive) {
    return (
      <RepeaterPreview
        element={element}
        templateChildren={children}
        label={label}
        rowDesignerId={rowDesignerId}
        rowVersion={rowVersion}
        list={list}
        showHeaders={showHeaders}
      />
    )
  }

  const error = bound ? interactiveProps?.errorByBindTo?.get(bindTo) : undefined
  const errorId = error ? `${element.id}-error` : undefined
  const onChange = interactiveProps?.onChange

  return (
    <RepeaterInteractive
      element={element}
      templateChildren={children}
      list={list}
      label={label}
      bindTo={bindTo}
      rowDesignerId={rowDesignerId}
      rowVersion={rowVersion}
      minRows={minRows}
      maxRows={maxRows}
      error={error}
      errorId={errorId}
      onChange={onChange}
      showHeaders={showHeaders}
    />
  )
}

function RepeaterPreview({
  element,
  templateChildren,
  label,
  rowDesignerId,
  rowVersion,
  list,
  showHeaders,
}: {
  element: DesignerElement
  templateChildren: DesignerElement[]
  label: string
  rowDesignerId: string
  rowVersion: number | undefined
  list: Record<string, unknown>[]
  showHeaders: boolean
}) {
  const { t } = useTranslation()
  if (showHeaders) {
    return (
      <RepeaterTablePreview
        element={element}
        templateChildren={templateChildren}
        label={label}
        rowDesignerId={rowDesignerId}
        rowVersion={rowVersion}
        list={list}
      />
    )
  }
  return (
    <div
      className={cn(
        'rounded border border-border',
        typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
      )}
      data-element-id={element.id}
    >
      {label !== '' && (
        <div className="border-b border-border bg-muted px-3 py-2 text-xs font-medium text-foreground">
          {label}
        </div>
      )}
      {list.length === 0 ? (
        <div className="flex items-center gap-2 px-3 py-2">
          <div className="flex flex-1 flex-col gap-1.5">
            {templateChildren.length === 0 ? (
              <span className="text-xs text-muted-foreground">
                {t('designer.renderer.configureRowTemplate')}
              </span>
            ) : (
              templateChildren.map((c) => <ElementRenderer key={c.id} element={c} interactive={false} />)
            )}
          </div>
          <Pencil className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />
          <Trash2 className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />
        </div>
      ) : (
        <ul className="divide-y divide-border">
          {list.map((row, idx) => (
            <li key={idx} className="flex items-center gap-2 px-3 py-2">
              <div className="flex flex-1 flex-col gap-1.5">
                {templateChildren.map((c) => (
                  <ElementRenderer
                    key={c.id}
                    element={c}
                    interactive={false}
                    interactiveProps={{
                      formData: row,
                      onChange: NOOP_FIELD_CHANGE,
                      onPrimaryButtonClick: NOOP_PRIMARY_CLICK,
                    }}
                  />
                ))}
              </div>
              <Pencil className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />
              <Trash2 className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />
            </li>
          ))}
        </ul>
      )}
      <div className="border-t border-border px-3 py-2">
        <button type="button" disabled className="text-xs text-muted-foreground">
          {t('designer.renderer.addPreview')}
        </button>
      </div>
      <div className="border-t border-border px-3 py-1.5 text-[10px] text-muted-foreground">
        {t('designer.renderer.rowFormStatus', {
          designer: rowDesignerId === '' ? t('designer.renderer.rowFormStatusNotSet') : rowDesignerId,
          version: rowVersion || t('designer.renderer.rowFormStatusVersionLatest'),
        })}
      </div>
    </div>
  )
}

function RepeaterTablePreview({
  element,
  templateChildren,
  label,
  rowDesignerId,
  rowVersion,
  list,
}: {
  element: DesignerElement
  templateChildren: DesignerElement[]
  label: string
  rowDesignerId: string
  rowVersion: number | undefined
  list: Record<string, unknown>[]
}) {
  const { t } = useTranslation()
  const fields = collectRepeaterFields(templateChildren)
  const hasFields = fields.length > 0
  return (
    <div
      className={cn(
        'rounded border border-border',
        typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
      )}
      data-element-id={element.id}
    >
      {label !== '' && (
        <div className="border-b border-border bg-muted px-3 py-2 text-xs font-medium text-foreground">
          {label}
        </div>
      )}
      {!hasFields ? (
        <p className="px-3 py-4 text-center text-xs text-muted-foreground">
          {t('designer.renderer.dropFieldsToTableHelp')}
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="border-b border-border bg-muted text-[11px] font-medium text-muted-foreground">
              <tr>
                {fields.map((f) => (
                  <th key={f.id} className="px-3 py-2 whitespace-nowrap">
                    {headerLabelOf(f)}
                  </th>
                ))}
                <th className="px-3 py-2 text-right whitespace-nowrap" aria-label={t('designer.renderer.actionsHeader')} />
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {list.length === 0 ? (
                <tr>
                  <td
                    colSpan={fields.length + 1}
                    className="px-3 py-4 text-center text-xs text-muted-foreground"
                  >
                    {t('designer.renderer.noRowsYet')}
                  </td>
                </tr>
              ) : (
                list.map((row, idx) => (
                  <tr key={idx}>
                    {fields.map((f) => (
                      <td key={f.id} className="px-3 py-2 align-top">
                        <ElementRenderer
                          element={f}
                          interactive={false}
                          interactiveProps={{
                            formData: row,
                            onChange: NOOP_FIELD_CHANGE,
                            onPrimaryButtonClick: NOOP_PRIMARY_CLICK,
                          }}
                        />
                      </td>
                    ))}
                    <td className="px-3 py-2 text-right">
                      <div className="inline-flex items-center gap-1">
                        <Pencil className="h-4 w-4 text-muted-foreground/50" aria-hidden />
                        <Trash2 className="h-4 w-4 text-muted-foreground/50" aria-hidden />
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      )}
      <div className="border-t border-border px-3 py-2">
        <button type="button" disabled className="text-xs text-muted-foreground">
          {t('designer.renderer.addPreview')}
        </button>
      </div>
      <div className="border-t border-border px-3 py-1.5 text-[10px] text-muted-foreground">
        {t('designer.renderer.rowFormStatus', {
          designer: rowDesignerId === '' ? t('designer.renderer.rowFormStatusNotSet') : rowDesignerId,
          version: rowVersion || t('designer.renderer.rowFormStatusVersionLatest'),
        })}
      </div>
    </div>
  )
}

function RepeaterInteractiveTable({
  templateChildren,
  list,
  canEdit,
  canDelete,
  editTitle,
  deleteTitle,
  onEdit,
  onDelete,
}: {
  templateChildren: DesignerElement[]
  list: Record<string, unknown>[]
  canEdit: boolean
  canDelete: boolean
  editTitle: string | undefined
  deleteTitle: string | undefined
  onEdit: (index: number) => void
  onDelete: (index: number) => void
}) {
  const { t } = useTranslation()
  const fields = collectRepeaterFields(templateChildren)
  if (fields.length === 0) {
    return (
      <p className="px-3 py-4 text-center text-xs text-muted-foreground">
        {t('designer.renderer.dropFieldsToTableHelp')}
      </p>
    )
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-xs">
        <thead className="border-b border-border bg-muted text-[11px] font-medium text-muted-foreground">
          <tr>
            {fields.map((f) => (
              <th key={f.id} className="px-3 py-2 whitespace-nowrap">
                {headerLabelOf(f)}
              </th>
            ))}
            <th className="px-3 py-2 text-right whitespace-nowrap" aria-label={t('designer.renderer.actionsHeader')} />
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {list.length === 0 ? (
            <tr>
              <td
                colSpan={fields.length + 1}
                className="px-3 py-4 text-center text-xs text-muted-foreground"
              >
                {t('designer.renderer.noRowsYet')}
              </td>
            </tr>
          ) : (
            list.map((row, idx) => (
              <tr key={idx}>
                {fields.map((f) => (
                  <td key={f.id} className="px-3 py-2 align-top">
                    <ElementRenderer
                      element={f}
                      interactive={false}
                      interactiveProps={{
                        formData: row,
                        onChange: NOOP_FIELD_CHANGE,
                        onPrimaryButtonClick: NOOP_PRIMARY_CLICK,
                      }}
                    />
                  </td>
                ))}
                <td className="px-3 py-2 text-right">
                  <div className="inline-flex items-center gap-1">
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span className="inline-flex">
                          <button
                            type="button"
                            onClick={() => onEdit(idx)}
                            disabled={!canEdit}
                            aria-label={t('designer.renderer.editRow')}
                            className="rounded p-1 text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active hover:text-primary disabled:cursor-not-allowed disabled:opacity-50"
                          >
                            <Pencil className="h-4 w-4" />
                          </button>
                        </span>
                      </TooltipTrigger>
                      {editTitle && <TooltipContent>{editTitle}</TooltipContent>}
                    </Tooltip>
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span className="inline-flex">
                          <button
                            type="button"
                            onClick={() => onDelete(idx)}
                            disabled={!canDelete}
                            aria-label={t('designer.renderer.deleteRow')}
                            className="rounded p-1 text-muted-foreground hover:bg-destructive/10 hover:text-destructive disabled:cursor-not-allowed disabled:opacity-50"
                          >
                            <Trash2 className="h-4 w-4" />
                          </button>
                        </span>
                      </TooltipTrigger>
                      {deleteTitle && <TooltipContent>{deleteTitle}</TooltipContent>}
                    </Tooltip>
                  </div>
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  )
}

function RepeaterInteractive({
  element,
  templateChildren,
  list,
  label,
  bindTo,
  rowDesignerId,
  rowVersion,
  minRows,
  maxRows,
  error,
  errorId,
  onChange,
  showHeaders,
}: {
  element: DesignerElement
  templateChildren: DesignerElement[]
  list: Record<string, unknown>[]
  label: string
  bindTo: string
  rowDesignerId: string
  rowVersion: number | undefined
  minRows: number | undefined
  maxRows: number | undefined
  error: string | undefined
  errorId: string | undefined
  onChange: ((bindTo: string, value: unknown) => void) | undefined
  showHeaders: boolean
}) {
  const { t } = useTranslation()
  const [modalOpen, setModalOpen] = useState(false)
  const [editingIndex, setEditingIndex] = useState<number | null>(null)

  // If the list shrinks while the drawer is open, list[editingIndex] becomes
  // undefined and a row-replace would silently no-op. Force-close the drawer
  // so the user doesn't lose their edit invisibly. Effect-based (rather than
  // setState-during-render) avoids React 18+ anti-pattern warnings and the
  // race with the drawer's 350ms close cleanup.
  useEffect(() => {
    if (
      modalOpen &&
      editingIndex !== null &&
      (editingIndex < 0 || editingIndex >= list.length)
    ) {
      setModalOpen(false)
      setEditingIndex(null)
    }
  }, [modalOpen, editingIndex, list.length])

  const closeModal = useCallback(() => {
    setModalOpen(false)
    setEditingIndex(null)
  }, [])

  const commitAdd = useCallback(
    (data: Record<string, unknown>) => {
      if (bindTo === '' || !onChange) return
      onChange(bindTo, [...list, data])
    },
    [bindTo, onChange, list],
  )

  const commitReplace = useCallback(
    (idx: number, data: Record<string, unknown>) => {
      if (bindTo === '' || !onChange) return
      onChange(bindTo, list.map((r, i) => (i === idx ? data : r)))
    },
    [bindTo, onChange, list],
  )

  const deleteRow = useCallback(
    (idx: number) => {
      if (bindTo === '' || !onChange) return
      // Hard-stop on min-rows: button is also disabled visually; defense-in-depth.
      if (minRows !== undefined && list.length <= minRows) return
      onChange(bindTo, list.filter((_, i) => i !== idx))
    },
    [bindTo, onChange, list, minRows],
  )

  const bound = bindTo !== ''
  const atMax = maxRows !== undefined && list.length >= maxRows
  const atMin = minRows !== undefined && list.length <= minRows
  const canEdit = bound && rowDesignerId !== ''
  const canDelete = bound && !atMin
  const canAdd = bound && rowDesignerId !== '' && !atMax

  const addTitle = !bound
    ? 'Configure Bind to first'
    : rowDesignerId === ''
      ? 'Configure Designer ID first'
      : atMax
        ? 'Maximum rows reached'
        : undefined
  const editTitle = !bound
    ? 'Configure Bind to first'
    : rowDesignerId === ''
      ? 'Configure Designer ID first'
      : undefined
  const deleteTitle = !bound
    ? 'Configure Bind to first'
    : atMin
      ? 'Minimum rows required'
      : undefined

  return (
    <div className="flex w-full flex-col gap-1">
      <div
        className={cn(
          'rounded border',
          error ? 'border-destructive ring-1 ring-destructive/40' : 'border-border',
          typeof element.properties.styleClasses === 'string' ? element.properties.styleClasses : undefined,
        )}
        data-element-id={element.id}
      >
        {label !== '' && (
          <div className="border-b border-border bg-muted px-3 py-2 text-xs font-medium text-foreground">
            {label}
          </div>
        )}
        {showHeaders ? (
          <RepeaterInteractiveTable
            templateChildren={templateChildren}
            list={list}
            canEdit={canEdit}
            canDelete={canDelete}
            editTitle={editTitle}
            deleteTitle={deleteTitle}
            onEdit={(idx) => {
              setEditingIndex(idx)
              setModalOpen(true)
            }}
            onDelete={deleteRow}
          />
        ) : list.length === 0 ? (
          <p className="px-3 py-4 text-center text-xs text-muted-foreground">{t('designer.renderer.noRowsYet')}</p>
        ) : (
          <ul className="divide-y divide-border">
            {list.map((row, idx) => (
              <li key={idx} className="flex items-center gap-2 px-3 py-2">
                <div className="flex flex-1 flex-col gap-1.5">
                  {templateChildren.length === 0 ? (
                    <span className="text-xs text-muted-foreground">
                      {t('designer.renderer.configureRowTemplate')}
                    </span>
                  ) : (
                    templateChildren.map((c) => (
                      <ElementRenderer
                        key={c.id}
                        element={c}
                        interactive={false}
                        interactiveProps={{
                          formData: row,
                          onChange: NOOP_FIELD_CHANGE,
                          onPrimaryButtonClick: NOOP_PRIMARY_CLICK,
                        }}
                      />
                    ))
                  )}
                </div>
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="inline-flex">
                      <button
                        type="button"
                        onClick={() => {
                          setEditingIndex(idx)
                          setModalOpen(true)
                        }}
                        disabled={!canEdit}
                        aria-label={t('designer.renderer.editRow')}
                        className="rounded p-1 text-muted-foreground hover:bg-overlay-hover hover:text-primary disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        <Pencil className="h-4 w-4" />
                      </button>
                    </span>
                  </TooltipTrigger>
                  {editTitle && <TooltipContent>{editTitle}</TooltipContent>}
                </Tooltip>
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="inline-flex">
                      <button
                        type="button"
                        onClick={() => deleteRow(idx)}
                        disabled={!canDelete}
                        aria-label={t('designer.renderer.deleteRow')}
                        className="rounded p-1 text-muted-foreground hover:bg-destructive/10 hover:text-destructive disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </span>
                  </TooltipTrigger>
                  {deleteTitle && <TooltipContent>{deleteTitle}</TooltipContent>}
                </Tooltip>
              </li>
            ))}
          </ul>
        )}
        <div className="border-t border-border px-3 py-2">
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="inline-flex">
                <button
                  type="button"
                  onClick={() => {
                    setEditingIndex(null)
                    setModalOpen(true)
                  }}
                  disabled={!canAdd}
                  className="rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground transition-colors hover:bg-primary-hover active:bg-primary-active disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {t('designer.renderer.addRow')}
                </button>
              </span>
            </TooltipTrigger>
            {addTitle && <TooltipContent>{addTitle}</TooltipContent>}
          </Tooltip>
        </div>
      </div>
      {error && (
        <p id={errorId} className={ERROR_MESSAGE_CLASS}>
          {error}
        </p>
      )}
      {modalOpen && (
        <RepeaterRowDrawer
          designerId={rowDesignerId}
          version={rowVersion}
          mode={editingIndex !== null ? 'edit' : 'add'}
          initialData={editingIndex !== null ? list[editingIndex] : {}}
          onSave={(data) => {
            if (editingIndex !== null) commitReplace(editingIndex, data)
            else commitAdd(data)
          }}
          onClose={closeModal}
        />
      )}
    </div>
  )
}

const NOOP_FIELD_CHANGE: (bindTo: string, value: unknown) => void = () => { }
const NOOP_PRIMARY_CLICK: () => void = () => { }
