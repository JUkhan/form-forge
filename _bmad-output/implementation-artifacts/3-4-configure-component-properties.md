# Story 3.4: Configure Component Properties

Status: done

## Story

As a Platform Admin,
I want to select any component on the canvas and edit its properties in a properties panel,
so that I can define how each field behaves and is stored.

## Acceptance Criteria

1. **AC-1 — Type-Specific Properties Panel** (ALREADY COMPLETE — DO NOT RE-IMPLEMENT)
   Given I click a component on the canvas
   When the properties panel opens
   Then the panel content is specific to the component type — `PropertyInspector.tsx` already has full per-type panel for all 15 component types

2. **AC-2 — fieldKey Required and Validated** ← THE ONLY NEW WORK IN THIS STORY
   Given an input-bearing leaf component (Text Input, TextArea, Number Input, Checkbox, Dropdown, DateTime Picker, Color Picker, Image, Repeater Field)
   When I edit its properties
   Then the `fieldKey` field is displayed, marked as required, and validates inline against the SQL-safe identifier regex: `^[a-z_][a-z0-9_]{0,62}$` (AR-4)

3. **AC-3 — Immediate Canvas Preview Update** (ALREADY COMPLETE — DO NOT RE-IMPLEMENT)
   Given I edit any property
   When the property changes
   Then the canvas preview updates immediately — Zustand `updateElementProperty` already triggers reactive re-renders

## Tasks / Subtasks

- [x] Task 1: Add `isValidFieldKey` utility and `FieldKeyField` component to `PropertyInspector.tsx` (AC-2)
  - [x] Add module-level function `isValidFieldKey(value: string): boolean` using regex `^[a-z_][a-z0-9_]{0,62}$`
  - [x] Add `FieldKeyField` component that renders a labeled input for `fieldKey` + inline validation error
  - [x] The input calls `updateProp(id, 'fieldKey', e.target.value)` on change (reactive; AC-3 is free)
  - [x] Show inline error text when value is empty OR when value is non-empty and fails regex
  - [x] Use i18n key `designer.inspector.fieldKeyField` for the field label (already exists: "Field key")
  - [x] Add i18n key `designer.inspector.fieldKeyInvalid` for the format error message (must be added)

- [x] Task 2: Wire `FieldKeyField` into the 9 input-bearing leaf cases in `PropertyFields` switch (AC-2)
  - [x] `'Text Input'` — add after the `LabelModeField`, before `Field label="Input type"`
  - [x] `'TextArea'` — add after `LabelModeField`, before `Field label="Placeholder"`
  - [x] `'Number Input'` — add after `LabelModeField`, before `Field label="Unit"`
  - [x] `'Checkbox'` — add after `Field label="Label"`, before `Field label="Bind to"`
  - [x] `'Dropdown'` — add after `LabelModeField`, before `Field label="Bind to"`
  - [x] `'DateTime Picker'` — add after `LabelModeField`, before `Field label="Format"`
  - [x] `'Color Picker'` — add after `LabelModeField`, before `Field label="Default color"`
  - [x] `'Image'` — add as the FIRST field (Image has no Label/LabelMode field)
  - [x] `'Repeater Field'` — add after `Field label="Field name"`, before the Header name block

- [x] Task 3: Add i18n key for validation error (AC-2)
  - [x] In `web/src/lib/i18n/locales/en.json` under `designer.inspector` — add `"fieldKeyInvalid": "Must start with a letter or underscore, use only lowercase letters, digits, and underscores, and be at most 63 characters."`

- [x] Task 4: Write unit tests for `isValidFieldKey` (AC-2)
  - [x] Create `web/src/components/designer/__tests__/fieldKeyValidation.test.ts` (or add to existing test file)
  - [x] Test all cases in the table under Dev Notes

- [x] Task 5: Build and verify
  - [x] `pnpm run build` — 0 errors
  - [x] `pnpm run lint` — no new errors beyond the 26 pre-existing `react-refresh` warnings
  - [x] `pnpm run test` — all tests pass

### Review Findings

_Code review (2026-05-23): 0 patch, 3 defer, 29 dismissed as noise (1 of which was a decision-needed item resolved by the author)._

- [x] [Review][Resolved-as-dismissed] **DateTime Picker & Color Picker — `FieldKeyField` placement ambiguity** — Spec Task 2 says "DateTime Picker: after `LabelModeField`, before `Field label="Format"`" and "Color Picker: after `LabelModeField`, before `Field label="Default color"`". Code at `web/src/components/designer/PropertyInspector.tsx:819` (DateTime) and `:860` (Color Picker) places `FieldKeyField` immediately after `LabelModeField`, with the existing `Bind to` Field (`:820`, `:861`) sitting between `FieldKeyField` and the spec's "before X" anchor. The placement falls within the spec's range constraint. Resolution (2026-05-23): the dev's uniform "right after `LabelModeField`" rule across all 7 LabelMode-bearing leaf cases is the intended reading; current code accepted, no patch. (source: auditor)

- [x] [Review][Defer] **Cross-element `fieldKey` collision detection** [`web/src/components/designer/PropertyInspector.tsx:130`] — Two leaves can share an identical `fieldKey`; downstream SQL column generation will collide. No inspector-level or form-level uniqueness check today. Explicitly out of scope per Dev Notes ("Do NOT add cross-element fieldKey validation … it belongs to 3.6"). Owner: Story 3.6 (Save Designer Version). (source: blind+edge)

- [x] [Review][Defer] **Save / dirty-state not gated on `isValidFieldKey`** [`web/src/components/designer/fieldKeyValidation.ts:6`] — Empty / invalid `fieldKey` is rendered as inline error only; no save path consumes `isValidFieldKey`. The function is referenced only by `FieldKeyField` for visual styling. Out of scope per Dev Notes ("Do NOT add save-blocking … it belongs to 3.6") and the file's own header comment ("Story 3.6 reuses this to gate Save"). Owner: Story 3.6. (source: edge)

- [x] [Review][Defer] **`FieldKeyField` a11y wiring is minimal** [`web/src/components/designer/PropertyInspector.tsx:140-158`] — `<Input>` is not linked to its `<Field label>` via `htmlFor`/`id`; no `aria-invalid`, no `aria-describedby` pointing at the error `<span>`, no `aria-live` region for error swap; error font size is `text-[10px]` (below WCAG body floor). Matches the pre-existing pattern used by every other `Field` + `Input` pair throughout `PropertyInspector.tsx` — not introduced by Story 3.4. Owner: Story 7.4 (Accessibility Compliance). (source: blind+edge)


### CRITICAL: AC-1 and AC-3 Are Already Complete — DO NOT Rewrite

`PropertyInspector.tsx` (`web/src/components/designer/PropertyInspector.tsx`, 1141 lines) was fully implemented in Story 3.1 and refined by the 3.1 code review (25 patches). It covers all 15 component types with per-type property editors.

`updateElementProperty` in `web/src/store/designerCanvas.ts` clones the tree, updates the target element's property, and calls `set({ rootElement: next, isDirty: true })`. Canvas re-renders reactively via Zustand subscriptions. AC-3 requires zero new code.

**The only deliverable in this story is AC-2: adding the `fieldKey` field with inline validation to 9 leaf component cases.**

---

### fieldKey vs bindTo — They Are SEPARATE Properties

Both exist in `DesignerElementProperties` (`web/src/types/designer.ts`):
```ts
bindTo?: string    // referenced by visibility/dependency conditions (hideWhenAll, hideWhenAny, dependsOn)
fieldKey?: string  // SQL column identifier; backend reads this from properties.fieldKey (AR-4)
```

**DO NOT rename or remove `bindTo`.** Visibility fields (`VisibilityFields` component) say "Comma-separated bindTo names" — they reference other components' `bindTo` values. Removing `bindTo` breaks visibility condition authoring.

**DO NOT add `fieldKey` to containers:** Stack, Row, Tabs, Repeater are structural and do not map to DB columns. Only the 9 input-bearing leaf types listed in AC-2 need `fieldKey`.

---

### SQL-Safe Identifier Regex (AR-4)

```
^[a-z_][a-z0-9_]{0,62}$
```

- Starts with lowercase letter or underscore
- Followed by 0–62 chars of `[a-z0-9_]`
- Max total length: 63 chars (PostgreSQL identifier limit)
- No uppercase, no hyphens, no spaces, no dots

**Test matrix for `isValidFieldKey`:**

| Input | Valid | Reason |
|---|---|---|
| `''` | false | empty — required |
| `'a'` | true | minimal valid |
| `'_foo'` | true | starts with underscore |
| `'abc_123'` | true | letters + digits + underscore |
| `'1abc'` | false | starts with digit |
| `'Abc'` | false | uppercase |
| `'with-hyphen'` | false | hyphen not in `[a-z0-9_]` |
| `'field key'` | false | space |
| `'field.key'` | false | dot |
| `'a'.repeat(63)` | true | exactly 63 chars |
| `'a'.repeat(64)` | false | exceeds 63 chars |
| `'_'` | true | single underscore (valid) |

---

### `FieldKeyField` Implementation Pattern

Follow the existing `Field` + `Input` pattern already in `PropertyInspector.tsx`. Below is the implementation to add:

```tsx
// Add at module level (below isValidFieldKey):
export function isValidFieldKey(value: string): boolean {
  return /^[a-z_][a-z0-9_]{0,62}$/.test(value)
}

// FieldKeyField component — add alongside other shared field components
function FieldKeyField({
  id,
  value,
  updateProp,
}: {
  id: string
  value: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const isEmpty = value === ''
  const isInvalid = !isEmpty && !isValidFieldKey(value)
  return (
    <Field label={`${t('designer.inspector.fieldKeyField')} *`}>
      <Input
        value={value}
        onChange={(e) => updateProp(id, 'fieldKey', e.target.value)}
        placeholder="e.g. employee_name"
        className={cn(INPUT_COMPACT_CLASS, (isEmpty || isInvalid) ? 'border-red-400' : '')}
      />
      {isEmpty && (
        <span className="text-[10px] text-red-600">{t('errors.required')}</span>
      )}
      {isInvalid && (
        <span className="text-[10px] text-red-600">{t('designer.inspector.fieldKeyInvalid')}</span>
      )}
    </Field>
  )
}
```

Notes:
- `useTranslation` is already imported at the top of `PropertyInspector.tsx`? **Check: it is NOT currently imported.** You must add `import { useTranslation } from 'react-i18next'` to `PropertyInspector.tsx`.
- Wait — `PropertyInspector.tsx` already imports from `react-i18next`? Check: it does NOT currently import `useTranslation`. Add the import.
- `cn` is already imported: `import { cn } from '@/lib/utils'`
- `INPUT_COMPACT_CLASS` constant is defined at the top: `const INPUT_COMPACT_CLASS = 'h-7 text-xs'`
- `errors.required` i18n key already exists: `"required": "This field is required."`
- `UpdateProp` type alias already exists: `type UpdateProp = (id: string, key: string, value: unknown) => void`

---

### Adding `useTranslation` to PropertyInspector.tsx

**Currently missing import.** At the top of `PropertyInspector.tsx`, add to imports:
```tsx
import { useTranslation } from 'react-i18next'
```

Then use `const { t } = useTranslation()` inside `FieldKeyField`.

**Important:** `useTranslation` must be called inside a component (React hook rules). Call it inside `FieldKeyField`, not at module level.

---

### Where to Insert `FieldKeyField` in PropertyFields Switch

Read `PropertyInspector.tsx` lines 418–1021. For each case, insert `FieldKeyField` immediately after the `LabelModeField` (or after `Label` when no `LabelModeField`). For `Image`, insert as the first child. For `Repeater Field`, insert after the `Field label="Field name"` block.

Usage pattern in each case:
```tsx
<FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
```

The `str('fieldKey')` helper (already in `PropertyFields`) returns `p['fieldKey']` as string, defaulting to `''` — correct for a required field with no default.

---

### Test File Location

The test infrastructure (Vitest + jsdom) was set up in Story 3.3. Use the same pattern:
- Test file: `web/src/components/designer/__tests__/fieldKeyValidation.test.ts`
- Import: `import { isValidFieldKey } from '../PropertyInspector'` — this requires `isValidFieldKey` to be **exported** (use `export function isValidFieldKey`).
- Test framework: Vitest globals (`describe`, `it`, `expect`) — already configured via `vitest.config.ts` + `tsconfig.app.json`

If the `__tests__` directory under `components/designer` does not exist, create it. The store tests live under `web/src/store/__tests__/`; component tests can live under `web/src/components/designer/__tests__/` per the module-folder structure.

---

### i18n Changes

File: `web/src/lib/i18n/locales/en.json`

Existing keys already present (DO NOT add again):
- `designer.inspector.fieldKeyField`: `"Field key"` ✓
- `errors.required`: `"This field is required."` ✓

**New key to add** under `designer.inspector`:
```json
"fieldKeyInvalid": "Must start with a letter or underscore, use only lowercase letters, digits, and underscores, and be at most 63 characters."
```

Final `designer.inspector` block should be:
```json
"inspector": {
  "title": "Properties",
  "noSelection": "Select an element to edit its properties.",
  "labelField": "Label",
  "fieldKeyField": "Field key",
  "fieldKeyInvalid": "Must start with a letter or underscore, use only lowercase letters, digits, and underscores, and be at most 63 characters.",
  "bindToField": "Bind to",
  "placeholderField": "Placeholder",
  "requiredField": "Required",
  "readOnlyField": "Read-only"
}
```

---

### Pre-existing Lint Warnings — Do Not Fix

`pnpm run lint` shows 26 pre-existing `react-refresh` plugin warnings in route files. These were present before this story and are NOT introduced by your changes. Do not fix them — that is out of scope.

---

### Story 3.6 Forward Note (Do NOT implement now)

Story 3.6 (Save Designer Version) will block saving when any input-bearing component has an empty `fieldKey`. The `isValidFieldKey` function you add in this story will be reused there. Do NOT add save-blocking or cross-element fieldKey validation in this story — it belongs to 3.6.

### Project Structure Notes

- `PropertyInspector.tsx` lives at `web/src/components/designer/PropertyInspector.tsx` — edit in place
- `en.json` lives at `web/src/lib/i18n/locales/en.json` — add one key
- Test file goes in `web/src/components/designer/__tests__/fieldKeyValidation.test.ts` (new file + new directory)
- Store (`web/src/store/designerCanvas.ts`) — NO changes needed
- Types (`web/src/types/designer.ts`) — NO changes needed (`fieldKey?: string` already in `DesignerElementProperties`)
- Route (`web/src/routes/_app/designer.$designerId.tsx`) — NO changes needed

### References

- AC-2 regex: Architecture AR-4 — `^[a-z_][a-z0-9_]{0,62}$`
- `PropertyInspector.tsx` full source: `web/src/components/designer/PropertyInspector.tsx` (1141 lines, read before editing)
- `DesignerElementProperties` type: `web/src/types/designer.ts` lines 3–23 (has `fieldKey?: string`)
- `updateElementProperty` action: `web/src/store/designerCanvas.ts` lines 140–150
- i18n: `web/src/lib/i18n/locales/en.json` lines 171–180 (existing `designer.inspector` block)
- Vitest config (Windows `pool: 'threads'`): `web/vite.config.ts` (must NOT use default `forks` pool — it intermittently times out on Windows)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (Claude Code, default model)

### Debug Log References

- Initial test run pre-implementation (RED): `pnpm exec vitest run src/components/designer/__tests__/fieldKeyValidation.test.ts` — 12/12 failed with `TypeError: isValidFieldKey is not a function`, confirming the test imports and matrix were exercising the to-be-implemented surface.
- Final post-implementation suite: `pnpm run build` → ✓ 0 errors; `pnpm run lint` → 26 errors (all pre-existing `react-refresh/only-export-components` in route files — unchanged count); `pnpm run test` → 28/28 passed (12 new + 16 from Story 3.3).

### Completion Notes List

1. **`isValidFieldKey` lives in its own module, not in `PropertyInspector.tsx`.** The story Dev Notes initially specified `export function isValidFieldKey` inside `PropertyInspector.tsx` with the test file importing from `'../PropertyInspector'`. Adding a non-component export to a component file triggers a new `react-refresh/only-export-components` lint error (would have made it 27, violating Task 5's "no new errors beyond the 26 pre-existing" constraint). Extracted to `web/src/components/designer/fieldKeyValidation.ts` and updated both the `PropertyInspector` import and the test import accordingly. This is the cleaner placement anyway since Story 3.6 will reuse the function from outside the inspector (save-blocking).
2. **`useTranslation` import added** to `PropertyInspector.tsx` — was previously absent. Hook is called inside `FieldKeyField` (per React rules), not at module level.
3. **Two error messages** render for `FieldKeyField`: `errors.required` when the value is empty, `designer.inspector.fieldKeyInvalid` when non-empty but fails the regex. Both render as `<span class="text-[10px] text-red-600">` below the input; the input also gets a `border-red-400` class in either state. Matches the inline-error pattern already used elsewhere in the inspector (e.g. Repeater min/max validation message at line 886 of the original file).
4. **AC-1 and AC-3 verified untouched** — `PropertyInspector.tsx` per-type panels remain in place for all 15 component types, and `updateElementProperty` in `designerCanvas.ts` is unchanged, so canvas reactivity continues to flow through Zustand subscriptions.
5. **Repeater Field placement nuance:** the story said "after `Field label="Field name"`, before the Header name block". The Header name block is the conditional `repeaterAncestor?.properties.showHeaders ? (...) : null` — `FieldKeyField` was inserted between the `</Field>` closing tag of Field name and the opening `{repeaterAncestor?.properties.showHeaders ? (` ternary so it is always visible regardless of the `showHeaders` flag (the field is required on every Repeater Field, not just tabular ones).
6. **No backend / type changes needed** — `fieldKey?: string` was already declared on `DesignerElementProperties` in `web/src/types/designer.ts` (line 9), and `updateElementProperty` reads/writes arbitrary keys via the `[key: string]: unknown` index signature.
7. **Test matrix coverage:** all 12 entries from the Dev Notes table are asserted (5 accept, 7 reject), using `it.each` to keep the assertions table-driven.

### File List

**Added:**
- `web/src/components/designer/fieldKeyValidation.ts` — exports `isValidFieldKey(value: string): boolean` (AR-4 regex `^[a-z_][a-z0-9_]{0,62}$`)
- `web/src/components/designer/__tests__/fieldKeyValidation.test.ts` — 12 unit tests covering the AR-4 regex matrix

**Modified:**
- `web/src/components/designer/PropertyInspector.tsx` — added `useTranslation` import, added `isValidFieldKey` import from `./fieldKeyValidation`, added `FieldKeyField` shared component (inline required + format validation), wired `FieldKeyField` into 9 leaf cases (`Text Input`, `TextArea`, `Number Input`, `Checkbox`, `Dropdown`, `DateTime Picker`, `Color Picker`, `Image`, `Repeater Field`)
- `web/src/lib/i18n/locales/en.json` — added `designer.inspector.fieldKeyInvalid` translation entry
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `3-4-configure-component-properties` transitioned `ready-for-dev` → `in-progress` → `review`
- `_bmad-output/implementation-artifacts/3-4-configure-component-properties.md` — Status updated to `review`; Tasks/Subtasks all checked; Dev Agent Record + Change Log filled in

## Change Log

| Date       | Change                                                                                                                    |
|------------|---------------------------------------------------------------------------------------------------------------------------|
| 2026-05-23 | Story 3.4 implementation — added `fieldKey` field with AR-4 regex validation (`isValidFieldKey`) to 9 input-bearing leaf component inspectors. Extracted validator to standalone `fieldKeyValidation.ts` module to keep `PropertyInspector.tsx` component-only (avoids react-refresh lint error). 12 new unit tests added. Build clean, lint count unchanged at 26 pre-existing errors, all 28 tests pass. |
