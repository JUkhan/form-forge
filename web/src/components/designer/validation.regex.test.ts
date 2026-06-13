import { describe, expect, it } from 'vitest'
import { validateForm } from './validation'
import type { DesignerElement } from '../../types/designer'

// Wraps a single Text Input leaf in a Stack root so validateForm can walk it.
function formWith(properties: Record<string, unknown>): DesignerElement {
  return {
    id: 'root',
    type: 'Stack',
    properties: {},
    children: [{ id: 'field-1', type: 'Text Input', properties }],
  }
}

describe('Text Input regex validation', () => {
  const props = {
    fieldKey: 'code',
    regexPattern: '[A-Za-z0-9]+',
    regexMessage: 'Letters and digits only',
  }

  it('passes when the value matches the pattern', () => {
    const result = validateForm(formWith(props), { code: 'abc123' })
    expect(result.isValid).toBe(true)
  })

  it('reports the custom message when the value does not match', () => {
    const result = validateForm(formWith(props), { code: 'has space' })
    expect(result.isValid).toBe(false)
    expect(result.errorByBindTo.get('code')).toBe('Letters and digits only')
  })

  it('enforces a whole-value (anchored) match, like HTML5 pattern', () => {
    // "abc!" contains a valid prefix but is not a full match.
    const result = validateForm(formWith(props), { code: 'abc!' })
    expect(result.isValid).toBe(false)
  })

  it('falls back to a generic message when no custom message is set', () => {
    const result = validateForm(
      formWith({ fieldKey: 'code', regexPattern: '\\d+' }),
      { code: 'nope' },
    )
    expect(result.errorByBindTo.get('code')).toBe('Invalid format')
  })

  it('skips the constraint for empty, non-required values', () => {
    const result = validateForm(formWith(props), { code: '' })
    expect(result.isValid).toBe(true)
  })

  it('treats an uncompilable pattern as no constraint (never throws)', () => {
    const result = validateForm(
      formWith({ fieldKey: 'code', regexPattern: '[' }),
      { code: 'anything' },
    )
    expect(result.isValid).toBe(true)
  })

  it('does nothing when no regexPattern is configured', () => {
    const result = validateForm(formWith({ fieldKey: 'code' }), { code: 'literally anything' })
    expect(result.isValid).toBe(true)
  })
})
