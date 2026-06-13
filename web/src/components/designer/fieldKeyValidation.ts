import type { DesignerElement } from '@/types/designer'

// AR-4 — SQL-safe identifier regex shared with the backend column-name
// validator. Story 3.6 reuses this to gate Save when any input-bearing leaf
// has an invalid or missing fieldKey; do not loosen without coordinating that
// change. Lives in its own module so PropertyInspector.tsx stays
// component-only (react-refresh/only-export-components).
export function isValidFieldKey(value: string): boolean {
  return /^[a-z_][a-z0-9_]{0,62}$/.test(value)
}

// Types that require a SQL-safe fieldKey at save time.
// MUST match the backend `FieldKeyValidator.InputBearingTypes` set exactly —
// the strings here are what `PropertyInspector.tsx` switch-case names emit and
// what gets stored in the canvas JSON. Note: `Repeater` IS in this set — its
// fieldKey names the one-to-many relation and is required. `Repeater Field` is
// NOT — it's a tabular display column that references a row-form field by
// `fieldName`, so it carries no fieldKey of its own.
export const INPUT_BEARING_TYPES: ReadonlySet<string> = new Set([
  'Text Input',
  'TextArea',
  'Number Input',
  'Checkbox',
  'Dropdown',
  'DateTime Picker',
  'Color Picker',
  // NOTE: `Image` is NOT input-bearing — it is a static display component (renders
  // `properties.src`, like `Label` renders `properties.text`) with no fieldKey and no
  // column. File uploads use the `File` component. Keep in sync with backend
  // FieldKeyValidator.InputBearingTypes.
  'Repeater',
  // TreeView binds selected node ids to fieldKey in view mode (mirrors Repeater).
  'TreeView',
])

// Past any legitimate designer tree by orders of magnitude. Guards the
// recursive walk against a corrupted-load case that would otherwise blow
// the JS engine stack and crash the SPA on Save. Mirrors the backend
// FieldKeyValidator.MaxDepth.
const MAX_DEPTH = 64

// Returns one error string per violation (missing fieldKey, invalid fieldKey,
// collision). Empty array means the tree is save-ready. All violations are
// returned together so the admin can fix everything in one pass instead of
// chasing one error per save attempt.
export function collectFieldKeyErrors(root: DesignerElement): string[] {
  const errors: string[] = []
  const seenKeys = new Map<string, string>()

  function walk(el: DesignerElement, depth: number) {
    if (depth >= MAX_DEPTH) {
      errors.push(`Component tree exceeds maximum depth of ${MAX_DEPTH}.`)
      return
    }
    if (INPUT_BEARING_TYPES.has(el.type)) {
      const rawKey = el.properties?.fieldKey
      const key = typeof rawKey === 'string' ? rawKey : ''
      // trim().length === 0 (not just length === 0): whitespace-only keys
      // would otherwise fall through to the regex path and surface as
      // `invalid field key: "   "` — misleading. Whitespace is "missing".
      if (key.trim().length === 0) {
        errors.push(`${el.type} (id: ${el.id}) is missing a field key.`)
      } else if (!isValidFieldKey(key)) {
        errors.push(`${el.type} (id: ${el.id}) has an invalid field key: "${key}".`)
      } else if (seenKeys.has(key)) {
        errors.push(
          `Field key "${key}" is used by multiple components (${seenKeys.get(key)!} and ${el.id}).`,
        )
      } else {
        seenKeys.set(key, el.id)
      }
    }
    for (const child of el.children ?? []) walk(child, depth + 1)
  }

  walk(root, 0)
  return errors
}
