// Pure helpers for the Dropdown's `optionsSource = 'dynamic'` path.
// Kept separate from ElementRenderer so the URL-templating + response-shape
// logic can be unit-tested in isolation.

// `{bindTo}` segment: matches a `{` followed by 1+ characters that are NOT '/'
// or '{' or '}' (so we don't accidentally chew across path segments or nested
// braces) and a closing `}`. The captured group is the bindTo key.
const PLACEHOLDER_RE = /\{([^/{}]+)\}/g

// Reject anything that looks like an absolute URL: leading scheme (`http://`,
// `https://`, `mailto:`, …), protocol-relative `//host/...`, or a bare `[a-z]+:`
// prefix. This prevents a malicious schema author from exfiltrating the JWT to
// an attacker-controlled domain since the http client unconditionally attaches
// `Authorization: Bearer <token>` to every request.
const ABSOLUTE_URL_RE = /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i

// `..` segments are dot-encoded as-is by `encodeURIComponent`, so a template
// like `/factories/../admin/users` (or a dependsOn value of `..`) survives both
// substitution and is then canonicalized by the browser to `/admin/users`,
// reaching endpoints the schema author didn't intend to surface. Reject before
// AND after substitution.
const DOT_SEGMENT_RE = /(?:^|\/)\.\.(?:\/|$)/

export function parseDependsOn(raw: unknown): string[] {
  if (raw === undefined || raw === null) return []
  return String(raw)
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '')
}

// One cascading-dependency mapping for a Designer-backed dropdown: the form's
// `local` field value filters the target designer's `target` column.
export interface DependsOnMapping {
  local: string
  target: string
}

// Parses the explicit cascading map "localField:targetColumn, l2:t2" into pairs.
// Entries without a colon, or with an empty side, are skipped.
export function parseDependsOnMap(raw: unknown): DependsOnMapping[] {
  if (raw === undefined || raw === null) return []
  const out: DependsOnMapping[] = []
  for (const part of String(raw).split(',')) {
    const idx = part.indexOf(':')
    if (idx === -1) continue
    const local = part.slice(0, idx).trim()
    const target = part.slice(idx + 1).trim()
    if (local !== '' && target !== '') out.push({ local, target })
  }
  return out
}

// Substitutes `{bindTo}` segments in `template` from `formData`, URL-encoding
// each value. Returns null when:
//   - the template is empty / whitespace-only,
//   - the template is an absolute URL (security),
//   - the template (or post-substitution path) contains `..` segments (security),
//   - any referenced bindTo is missing/null/empty (the renderer treats null as
//     "dependencies not yet ready" and suppresses the fetch).
export function resolveApiPath(
  template: string,
  formData: Record<string, unknown>,
): string | null {
  const t = typeof template === 'string' ? template.trim() : ''
  if (t === '') return null
  if (ABSOLUTE_URL_RE.test(t)) return null
  if (DOT_SEGMENT_RE.test(t)) return null
  // Require a leading `/` so relative paths can't resolve against the current
  // page URL (e.g. `apiPath: 'factories'` on /designer/abc would hit
  // /designer/factories, returning a misleading 404).
  if (!t.startsWith('/')) return null

  let allReady = true
  const resolved = t.replace(PLACEHOLDER_RE, (_, key: string) => {
    const v = formData[key]
    if (v === undefined || v === null || v === '') {
      allReady = false
      return ''
    }
    return encodeURIComponent(String(v))
  })

  if (!allReady) return null
  // Re-check: an interpolated `..` survives encodeURIComponent (dots aren't
  // encoded) and would slip past the up-front guard.
  if (DOT_SEGMENT_RE.test(resolved)) return null
  return resolved
}

export interface DropdownOption {
  label: string
  value: string
}

// Common platform endpoints return `PaginatedList<T>` (`{ items, totalCount }`)
// or sibling shapes. Auto-unwrap these envelopes before the array check so
// authors can target `/factories` directly without writing a bare-array adapter.
const ENVELOPE_KEYS = ['items', 'data', 'results'] as const

// Coerces an arbitrary API response into the canonical option shape the
// renderer paints. Graceful by design: unrecognized inputs return [];
// items missing the configured `labelField` fall back to the value so the
// user sees the value as both label and submit value rather than blank.
export function extractOptions(
  response: unknown,
  labelField: string,
  valueField: string,
): DropdownOption[] {
  let arr: unknown = response
  if (!Array.isArray(arr) && arr !== null && typeof arr === 'object') {
    const env = arr as Record<string, unknown>
    for (const k of ENVELOPE_KEYS) {
      if (Array.isArray(env[k])) {
        arr = env[k]
        break
      }
    }
  }
  if (!Array.isArray(arr)) return []

  const lf = typeof labelField === 'string' ? labelField.trim() : ''
  const vf = typeof valueField === 'string' ? valueField.trim() : ''

  const out: DropdownOption[] = []
  const seen = new Set<string>()
  for (const item of arr) {
    if (item === null || item === undefined) continue
    let label: string
    let value: string
    if (typeof item === 'object') {
      const rec = item as Record<string, unknown>
      const rawValue = vf !== '' ? rec[vf] : undefined
      const rawLabel = lf !== '' ? rec[lf] : undefined
      // Object item with no resolvable value field would render as
      // `[object Object]` and collide with every other unmapped row on its
      // React key. Drop it instead of producing a corrupted option list.
      if (rawValue === undefined || rawValue === null) continue
      value = String(rawValue)
      label =
        rawLabel !== undefined && rawLabel !== null && String(rawLabel) !== ''
          ? String(rawLabel)
          : value
    } else {
      // Array of strings (or numbers, booleans) — use String(item) for both.
      const s = String(item)
      label = s
      value = s
    }
    // Empty-string value collides with the placeholder `<option value="">`,
    // so a user's selection visually disappears. Skip the row.
    if (value === '') continue
    // Dedupe by value to avoid React duplicate-key warnings when the API
    // legitimately returns repeated values (or two objects share a value field).
    if (seen.has(value)) continue
    seen.add(value)
    out.push({ label, value })
  }
  return out
}
