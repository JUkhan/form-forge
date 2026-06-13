import { getFieldKey, type DesignerElement } from '../../types/designer'

// Parses a comma-separated list of dependency field bindTo names from a
// `hideWhenAll` / `hideWhenAny` property value. Mirrors parseDependsOn so
// authors only learn one syntax across the inspector.
export function parseVisibilityList(raw: unknown): string[] {
  if (raw === undefined || raw === null) return []
  return String(raw)
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '')
}

export interface VisibilityState {
  // Element ids whose render path must short-circuit to null in interactive mode.
  hiddenElementIds: Set<string>
  // bindTo names whose owning element is hidden — used by submission to omit
  // the key from the payload and by validation to skip the rule walk.
  hiddenBindTos: Set<string>
}

// Stable identity for "nothing is hidden" so the consumer's useMemo deps that
// depend on this object don't invalidate every render of a no-condition form.
export const EMPTY_VISIBILITY: VisibilityState = {
  hiddenElementIds: new Set(),
  hiddenBindTos: new Set(),
}

// A condition references a bindTo name; the condition is met when that bindTo
// currently holds a "real" value. The rule mirrors the dropdown's "Depends on"
// notion of readiness (`undefined / null / ''` → not ready) and additionally
// excludes `false` (unchecked Checkbox) and `[]` (empty Repeater list) so
// authoring "hide when the checkbox is checked" / "hide once the repeater has
// rows" behaves as users intuitively expect. `0` deliberately counts as a
// value — a user typing 0 into a Number Input made a real choice.
function hasValue(v: unknown): boolean {
  if (v === undefined || v === null || v === '') return false
  if (v === false) return false
  if (Array.isArray(v) && v.length === 0) return false
  return true
}

interface IndexedTree {
  byId: Map<string, DesignerElement>
  bindToOwner: Map<string, string>
  ownerBindTo: Map<string, string>
}

function indexTree(root: DesignerElement): IndexedTree {
  const byId = new Map<string, DesignerElement>()
  const bindToOwner = new Map<string, string>()
  const ownerBindTo = new Map<string, string>()
  function walk(el: DesignerElement, insideRepeater: boolean): void {
    byId.set(el.id, el)
    // Use the canonical fieldKey (with legacy bindTo fallback). Variable
    // names keep the internal "bindTo" naming because the resolved value
    // serves the same role — the form-state key that visibility rules
    // reference.
    const bindTo = getFieldKey(el.properties)
    // Repeater children sit in row scope, not outer-form scope — so their
    // field keys must not be referenceable from sibling/cousin visibility
    // rules in the outer schema.
    if (bindTo !== '' && !insideRepeater) {
      bindToOwner.set(bindTo, el.id)
      ownerBindTo.set(el.id, bindTo)
    }
    // DatasetComponent children are its own filter-form inputs (view-local), so like
    // Repeater row children they must not be referenceable from outer visibility rules.
    const childInsideRepeater =
      insideRepeater || el.type === 'Repeater' || el.type === 'DatasetComponent'
    for (const c of el.children ?? []) walk(c, childInsideRepeater)
  }
  walk(root, false)
  return { byId, bindToOwner, ownerBindTo }
}

// Computes which elements should be hidden given the current form state.
// Visibility is iterative: element A's `hideWhenAll = ['b']` flips when B
// gains a value, but B itself may carry visibility rules referencing A. We
// run a fixed-point loop that only marks elements hidden (never unhides
// them in-loop), so termination is bounded by the element count. A cycle
// like "A hides on B" + "B hides on A" stabilizes immediately: a reference
// to a hidden element counts as "condition not met" per the spec, so
// neither ever flips on the first pass.
export function computeVisibility(
  root: DesignerElement | null,
  formData: Record<string, unknown>,
): VisibilityState {
  if (!root) return EMPTY_VISIBILITY

  const { byId, bindToOwner, ownerBindTo } = indexTree(root)
  const hidden = new Set<string>()
  const maxIter = byId.size + 1

  for (let i = 0; i < maxIter; i++) {
    const before = hidden.size
    for (const [id, el] of byId) {
      if (hidden.has(id)) continue
      const p = el.properties ?? {}
      const all = parseVisibilityList(p.hideWhenAll)
      const any = parseVisibilityList(p.hideWhenAny)
      if (all.length === 0 && any.length === 0) continue

      const conditionMet = (name: string): boolean => {
        const ownerId = bindToOwner.get(name)
        if (ownerId === undefined) return false
        if (hidden.has(ownerId)) return false
        return hasValue(formData[name])
      }

      const allFires = all.length > 0 && all.every(conditionMet)
      const anyFires = any.length > 0 && any.some(conditionMet)
      if (allFires || anyFires) hidden.add(id)
    }
    if (hidden.size === before) break
  }

  if (hidden.size === 0) return EMPTY_VISIBILITY

  const hiddenBindTos = new Set<string>()
  for (const id of hidden) {
    const bt = ownerBindTo.get(id)
    if (bt !== undefined) hiddenBindTos.add(bt)
  }
  return { hiddenElementIds: hidden, hiddenBindTos }
}
