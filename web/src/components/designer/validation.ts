import { getFieldKey, type DesignerElement } from '../../types/designer'
import { resolveTextInputType } from './textInputTypes'

export type FieldError = { bindTo: string; elementId: string; message: string }
export type ValidationResult = {
  isValid: boolean
  errors: FieldError[]
  errorByBindTo: Map<string, string>
}

// Shared "no schema, no errors" result. Hoisted so renderers can use a stable identity
// reference — a fresh `new Map()` per render would invalidate downstream `useMemo` deps.
export const EMPTY_VALIDATION: ValidationResult = {
  isValid: true,
  errors: [],
  errorByBindTo: new Map(),
}

const CONTAINER_TYPES = new Set(['Stack', 'Row', 'Tabs'])

// HTML5 spec regex for `<input type=email>` :invalid matching (per WHATWG HTML §4.10.5.1.5).
// Chosen over RFC 5322 because users see the rendered DOM as `type=email`, so error parity
// with browser-native validation is the least-surprising contract. Single-address only
// (no comma-separated lists; `multiple` attribute isn't exposed by the schema).
const EMAIL_REGEX =
  /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/

// Use the platform URL parser instead of a regex — covers the WHATWG URL spec including
// IDN, IPv6 hosts, port ranges, and percent-encoding without us re-implementing it.
// Narrower than browser-native `<input type=url>` parity: we additionally require the
// parsed `protocol` to be in the allowlist below. Browser-native `<input type=url>`
// accepts ANY absolute URL with scheme — including `javascript:`, `data:`, `file:`,
// `tel:`, custom schemes — which would let the validator silently sanction inputs that
// downstream consumers may render as href / location, opening an XSS sink.
const URL_ALLOWED_PROTOCOLS = new Set(['http:', 'https:', 'mailto:'])
function isValidUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw)
    return URL_ALLOWED_PROTOCOLS.has(parsed.protocol)
  } catch {
    return false
  }
}

// Compile a schema-author-supplied validation regex with HTML5 `pattern`-attribute
// semantics: the pattern must match the WHOLE value (implicitly anchored via `^(?:…)$`),
// so authors get the same behaviour they'd expect from a native `<input pattern>`.
// Invalid patterns compile to `null` and are treated as "no constraint" rather than
// throwing mid-render. Results are cached by pattern string because validation re-runs
// on every keystroke across the whole form.
const patternCache = new Map<string, RegExp | null>()
function compilePattern(pattern: string): RegExp | null {
  const cached = patternCache.get(pattern)
  if (cached !== undefined) return cached
  let compiled: RegExp | null
  try {
    compiled = new RegExp(`^(?:${pattern})$`)
  } catch {
    compiled = null
  }
  patternCache.set(pattern, compiled)
  return compiled
}

export function getFieldLabel(el: DesignerElement, fallback: string = 'Field'): string {
  const raw = el.properties?.label
  if (typeof raw === 'string' && raw.trim() !== '') return raw
  return fallback
}

// Validation only runs in DynamicComponent's interactive mode. The designer canvas
// renders ElementRenderer without interactiveProps, so errorByBindTo is undefined
// there and no error UI is shown.
export function validateForm(
  root: DesignerElement,
  formData: Record<string, unknown>,
  hiddenElementIds?: Set<string>,
): ValidationResult {
  const errors: FieldError[] = []
  const errorByBindTo = new Map<string, string>()
  walk(root, formData, errors, errorByBindTo, hiddenElementIds)
  // Hit the EMPTY_VALIDATION fast path so a valid form returns the same identity every
  // render — keeps `interactiveProps` / `ElementRenderer` memoization from invalidating
  // on every keystroke.
  if (errors.length === 0) return EMPTY_VALIDATION
  return { isValid: false, errors, errorByBindTo }
}

// True if any leaf inside this subtree has an entry in `errorByBindTo`. Used by the Tabs
// renderer to badge headers whose hidden subtree contains a validation error.
//
// Mirrors DynamicComponent.extractBindToKeys' Repeater early-return: Repeater children
// bind to row scope, not outer-form scope, so walking past a Repeater would match an
// outer-form error keyed against a row-template field name.
export function subtreeHasErrors(
  el: DesignerElement,
  errorByBindTo: Map<string, string> | undefined,
): boolean {
  if (!errorByBindTo || errorByBindTo.size === 0) return false
  // Canonical fieldKey (with legacy bindTo fallback). The error map's keys
  // were seeded by walk() below using the same lookup, so they line up.
  const bindTo = getFieldKey(el.properties)
  if (bindTo !== '' && errorByBindTo.has(bindTo)) return true
  if (el.type === 'Repeater') return false
  // A DatasetComponent's children are its own filter-form inputs (view-local), not
  // outer-form fields — walk() never validates them, so they can't carry an error.
  if (el.type === 'DatasetComponent') return false
  for (const child of el.children ?? []) {
    if (subtreeHasErrors(child, errorByBindTo)) return true
  }
  return false
}

function walk(
  el: DesignerElement,
  formData: Record<string, unknown>,
  errors: FieldError[],
  errorByBindTo: Map<string, string>,
  hiddenElementIds: Set<string> | undefined,
): void {
  // Hidden elements (and their subtree) are excluded from form submission —
  // validating them would block the form on rules the user can't even see.
  // Containers and leaves both short-circuit here so a hidden Stack also
  // suppresses its descendants' rules.
  if (hiddenElementIds?.has(el.id)) return

  // A DatasetComponent's children are filter-form inputs scoped to that view, not
  // record fields — skip the whole subtree so a required filter input can't block
  // the surrounding form's save (mirrors the Repeater leaf-only handling).
  if (el.type === 'DatasetComponent') return

  if (CONTAINER_TYPES.has(el.type)) {
    for (const child of el.children ?? []) {
      walk(child, formData, errors, errorByBindTo, hiddenElementIds)
    }
    return
  }

  const bindTo = getFieldKey(el.properties)
  if (bindTo === '') return

  const v = formData[bindTo]
  const message = ruleFor(el, v)
  if (message !== null) {
    errors.push({ bindTo, elementId: el.id, message })
    if (!errorByBindTo.has(bindTo)) {
      errorByBindTo.set(bindTo, message)
    }
  }
}

// Returns the first rule violation for a leaf, in the order:
// required → range (min/max, minRows/maxRows) → length (maxLength).
function ruleFor(el: DesignerElement, v: unknown): string | null {
  const p = el.properties ?? {}
  const required = p.required === true

  switch (el.type) {
    // TextArea shares Text Input's required + maxLength logic; falling through avoids
    // duplication and silently keeps both types in sync if a future story tweaks the
    // shared rules. The email/url format checks below dispatch on resolveTextInputType,
    // which TextArea never sets — so the format branch is a no-op for TextArea values.
    case 'TextArea':
    case 'Text Input': {
      if (required && (v === undefined || v === null || v === '')) {
        return `${getFieldLabel(el, 'This field')} is required`
      }
      const maxLength = toFiniteNumber(p.maxLength)
      if (
        maxLength !== null &&
        typeof v === 'string' &&
        v.length > maxLength
      ) {
        return `Must be ${maxLength} characters or fewer`
      }
      // Format rules — only apply when a non-empty string is present. Empty + not-required
      // intentionally bypasses (the required check above already covered required + empty),
      // matching the browser-native convention "you don't have to fill it, but if you do, it
      // must be valid".
      if (typeof v === 'string' && v !== '') {
        const inputType = resolveTextInputType(p)
        if (inputType === 'email' && !EMAIL_REGEX.test(v)) {
          return 'Must be a valid email address'
        }
        if (inputType === 'url' && !isValidUrl(v)) {
          return 'Must be a valid URL'
        }
        // Author-supplied regex constraint. Runs after the built-in format checks so
        // an `email`/`url` field can still layer a stricter custom pattern on top. A
        // pattern that fails to compile yields `null` (no constraint) and is skipped.
        const pattern = typeof p.regexPattern === 'string' ? p.regexPattern.trim() : ''
        if (pattern !== '') {
          const re = compilePattern(pattern)
          if (re !== null && !re.test(v)) {
            const customMsg = typeof p.regexMessage === 'string' ? p.regexMessage.trim() : ''
            return customMsg !== '' ? customMsg : 'Invalid format'
          }
        }
      }
      return null
    }
    case 'Number Input': {
      if (
        required &&
        (v === undefined || v === null || (typeof v === 'number' && Number.isNaN(v)))
      ) {
        return `${getFieldLabel(el, 'This field')} is required`
      }
      const min = toFiniteNumber(p.min)
      if (min !== null && typeof v === 'number' && v < min) {
        return `Must be at least ${min}`
      }
      const max = toFiniteNumber(p.max)
      if (max !== null && typeof v === 'number' && v > max) {
        return `Must be at most ${max}`
      }
      return null
    }
    case 'Dropdown': {
      if (required && (v === undefined || v === null || v === '')) {
        return `${getFieldLabel(el, 'Please select')} is required`
      }
      return null
    }
    case 'DateTime Picker': {
      if (required && (v === undefined || v === null || v === '')) {
        return `${getFieldLabel(el, 'Date')} is required`
      }
      return null
    }
    case 'Checkbox': {
      if (required && v !== true) {
        return `${getFieldLabel(el, 'This option')} must be checked`
      }
      return null
    }
    case 'Repeater': {
      // Prefer the more specific "At least N rows" message over the generic
      // "is required" when minRows >= 1 — `required` reduces to "minRows: 1"
      // semantically, so the row-count message is strictly more informative.
      const minRows = toFiniteNumber(p.minRows)
      const actualLen = Array.isArray(v) ? v.length : 0
      if (minRows !== null && minRows > 0 && actualLen < minRows) {
        return `At least ${minRows} ${minRows === 1 ? 'row' : 'rows'} required`
      }
      if (required && (!Array.isArray(v) || v.length === 0)) {
        return `${getFieldLabel(el, 'This field')} is required`
      }
      const maxRows = toFiniteNumber(p.maxRows)
      if (maxRows !== null && Array.isArray(v) && v.length > maxRows) {
        return `At most ${maxRows} ${maxRows === 1 ? 'row' : 'rows'} allowed`
      }
      return null
    }
    default:
      return null
  }
}

function toFiniteNumber(raw: unknown): number | null {
  if (typeof raw === 'number' && Number.isFinite(raw)) return raw
  if (typeof raw === 'string' && raw.trim() !== '') {
    const n = Number(raw)
    if (Number.isFinite(n)) return n
  }
  return null
}
