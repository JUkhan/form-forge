// Field "map expression" — an optional author-supplied JS expression that
// transforms a Field element's own value before it is displayed (e.g. for a
// field `name`, the expression `name.toUpperCase()` renders the upper-cased
// value). Used by the Repeater Field leaf (ElementRenderer) and by the
// record-list table (RecordListPage) so the same expression drives both the
// form/preview render and the CRUD table column.
//
// Trust model: designer schemas are authored by trusted internal users — the
// SAME boundary the RawHtml element and the Repeater Field inline-style already
// rely on (both inject author-controlled content verbatim). The expression is
// compiled with `new Function`, so a bad/hostile expression is no more powerful
// than the RawHtml the same author can already drop on the canvas. Every call is
// wrapped so a throwing or non-compiling expression NEVER breaks a render — the
// caller falls back to the raw value.

// A compiled expression: takes the field's raw value, returns the mapped value.
// Compilation failures yield a function that just echoes the value back, so the
// call site needs no extra guard for the compile step.
export type CompiledMapExpression = (value: unknown) => unknown

// The field's value is bound to TWO identifiers inside the expression:
//   - its `fieldName` (so the author can write `name.toUpperCase()`), and
//   - a generic `value` alias (so `value.toUpperCase()` works too).
// fieldKeys are SafeIdentifier snake_case strings, hence valid JS identifiers;
// when fieldName collides with `value` we only declare the one parameter.
export function compileMapExpression(
  expression: string,
  fieldName: string,
): CompiledMapExpression {
  const expr = expression.trim()
  if (expr === '') return (value) => value

  const paramNames = fieldName && fieldName !== 'value' ? [fieldName, 'value'] : ['value']
  let fn: (...args: unknown[]) => unknown
  try {
    fn = new Function(...paramNames, `return (${expr})`) as (...args: unknown[]) => unknown
  } catch {
    // Syntax error in the expression — degrade to identity so the raw value shows.
    return (value) => value
  }

  return (value: unknown) => {
    try {
      return paramNames.length === 2 ? fn(value, value) : fn(value)
    } catch {
      // Runtime error (e.g. calling a string method on null) — show the raw value.
      return value
    }
  }
}
