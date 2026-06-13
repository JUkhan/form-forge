// HTML input types exposed by the Text Input element's `inputType` property.
// Single source of truth shared by:
//   - PropertyInspector (renders <option> from this list + coerces unknown
//     legacy values to 'text' so the controlled <select> stays in sync)
//   - ElementRenderer   (whitelists the rendered DOM `type` attribute)
//   - validation        (normalizes the inputType before format-rule dispatch)
export const ALLOWED_TEXT_INPUT_TYPES = ['text', 'email', 'password', 'url'] as const
export type AllowedTextInputType = (typeof ALLOWED_TEXT_INPUT_TYPES)[number]

export const TEXT_INPUT_TYPE_LABELS: Record<AllowedTextInputType, string> = {
  text: 'Text',
  email: 'Email',
  password: 'Password',
  url: 'URL',
}

// Helper name uses `Text` (not the more obvious `resolveInputType`) because the
// DateTime Picker branch in ElementRenderer declares a block-scoped `const inputType`
// — a top-level `resolveInputType` import would shadow it and trip no-shadow.
export function resolveTextInputType(p: Record<string, unknown>): AllowedTextInputType {
  const raw = typeof p.inputType === 'string' ? p.inputType : 'text'
  return (ALLOWED_TEXT_INPUT_TYPES as readonly string[]).includes(raw)
    ? (raw as AllowedTextInputType)
    : 'text'
}
