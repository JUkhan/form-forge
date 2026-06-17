// Parameterized-query feature — binds each {_placeholder} of a parameterized "query"-type
// dataset to a filter-input's fieldKey (the value is supplied at runtime by that input).
// Distinct from datasetFilter.ts: a parameter binding has NO operator/condition — it is just
// "this placeholder gets its value from this field". Extra WHERE conditions on the dataset's
// output columns stay in datasetFilter.ts.

import { uid } from '@/lib/utils'
import type { DesignerElement } from '@/types/designer'

export type QueryParamBinding = {
  id: string
  param: string // placeholder name, e.g. "_age"
  valueFieldKey: string // '' until bound; the field key whose value fills the placeholder
}

export function newBindingId(): string {
  return uid('qpb')
}

// Normalize a persisted/unknown value into a well-formed binding list. Anything malformed
// collapses to an empty list so the inspector never crashes on bad data.
export function parseQueryParamBindings(raw: unknown): QueryParamBinding[] {
  if (!Array.isArray(raw)) return []
  const out: QueryParamBinding[] = []
  for (const it of raw) {
    if (it === null || typeof it !== 'object') continue
    const c = it as Record<string, unknown>
    out.push({
      id: typeof c.id === 'string' && c.id !== '' ? c.id : newBindingId(),
      param: typeof c.param === 'string' ? c.param : '',
      valueFieldKey: typeof c.valueFieldKey === 'string' ? c.valueFieldKey : '',
    })
  }
  return out
}

// ── immutable editor ops ─────────────────────────────────────────────────────────────

export function addQueryParamBinding(
  list: QueryParamBinding[],
  param = '',
): QueryParamBinding[] {
  return [...list, { id: newBindingId(), param, valueFieldKey: '' }]
}

export function removeQueryParamBinding(list: QueryParamBinding[], id: string): QueryParamBinding[] {
  return list.filter((b) => b.id !== id)
}

export function updateQueryParamBinding(
  list: QueryParamBinding[],
  id: string,
  patch: Partial<Omit<QueryParamBinding, 'id'>>,
): QueryParamBinding[] {
  return list.map((b) => (b.id === id ? { ...b, ...patch } : b))
}

// ── validation + runtime resolution ──────────────────────────────────────────────────

// Placeholders not yet covered by a binding (a binding that names the placeholder AND has a
// bound value field). The dataset can't be used until this is empty.
export function unmappedParams(
  bindings: QueryParamBinding[],
  placeholders: readonly string[],
): string[] {
  return placeholders.filter(
    (ph) => !bindings.some((b) => b.param === ph && b.valueFieldKey !== ''),
  )
}

// Build the queryParameters JSON object string from the bindings + live field values. Returns
// undefined when ANY placeholder lacks a bound, non-empty value — so the caller can hold off
// the request (the server requires every parameter).
export function resolveQueryParameters(
  bindings: QueryParamBinding[],
  placeholders: readonly string[],
  values: Record<string, unknown>,
): string | undefined {
  if (placeholders.length === 0) return undefined
  const out: Record<string, string> = {}
  for (const ph of placeholders) {
    const binding = bindings.find((b) => b.param === ph && b.valueFieldKey !== '')
    if (!binding) return undefined
    const raw = values[binding.valueFieldKey]
    if (raw === undefined || raw === null || raw === '') return undefined
    out[ph] = String(raw)
  }
  return JSON.stringify(out)
}

// Collect every DatasetComponent in the tree (used by the designer's save-time validation).
export function collectDatasetComponents(root: DesignerElement | null): DesignerElement[] {
  const out: DesignerElement[] = []
  function walk(el: DesignerElement) {
    if (el.type === 'DatasetComponent') out.push(el)
    for (const c of el.children ?? []) walk(c)
  }
  if (root) walk(root)
  return out
}
