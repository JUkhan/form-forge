import { describe, it, expect } from 'vitest'
import type { DesignerElement } from '@/types/designer'
import {
  parsePgType,
  serializePgType,
  validatePgType,
  collectPgTypeErrors,
  DEFAULT_PGTYPE_BY_TYPE,
} from '../pgTypes'

describe('parsePgType', () => {
  it('parses numeric with precision and scale', () => {
    expect(parsePgType('numeric(10,2)')).toEqual({ base: 'numeric', precision: 10, scale: 2 })
  })
  it('parses varchar/char length', () => {
    expect(parsePgType('varchar(64)')).toEqual({ base: 'varchar', length: 64 })
    expect(parsePgType('char(10)')).toEqual({ base: 'char', length: 10 })
  })
  it('parses bare types', () => {
    expect(parsePgType('text')).toEqual({ base: 'text' })
    expect(parsePgType('timestamptz')).toEqual({ base: 'timestamptz' })
  })
  it('returns empty base for empty/undefined', () => {
    expect(parsePgType('')).toEqual({ base: '' })
    expect(parsePgType(undefined)).toEqual({ base: '' })
  })
})

describe('serializePgType', () => {
  it('round-trips parametrized types', () => {
    expect(serializePgType({ base: 'numeric', precision: 10, scale: 2 })).toBe('numeric(10,2)')
    expect(serializePgType({ base: 'varchar', length: 64 })).toBe('varchar(64)')
  })
  it('serializes incomplete params to the bare base (so validation flags them)', () => {
    expect(serializePgType({ base: 'numeric' })).toBe('numeric')
    expect(serializePgType({ base: 'varchar' })).toBe('varchar')
  })
  it('serializes bare types as-is', () => {
    expect(serializePgType({ base: 'boolean' })).toBe('boolean')
  })
})

describe('validatePgType', () => {
  it('accepts valid types', () => {
    for (const t of ['text', 'varchar(255)', 'char(10)', 'numeric(10,2)', 'integer', 'boolean', 'uuid', 'timestamptz', 'double precision', 'jsonb']) {
      expect(validatePgType(t)).toBeNull()
    }
  })
  it('rejects empty/missing', () => {
    expect(validatePgType('')).not.toBeNull()
    expect(validatePgType(undefined)).not.toBeNull()
  })
  it('rejects incomplete parametrized types', () => {
    expect(validatePgType('numeric')).not.toBeNull()
    expect(validatePgType('varchar')).not.toBeNull()
  })
  it('rejects out-of-range params', () => {
    expect(validatePgType('numeric(0,0)')).not.toBeNull()
    expect(validatePgType('numeric(5,9)')).not.toBeNull()
    expect(validatePgType('varchar(0)')).not.toBeNull()
  })
  it('rejects unknown types', () => {
    expect(validatePgType('bogus')).not.toBeNull()
    expect(validatePgType('int')).not.toBeNull()
  })
})

describe('collectPgTypeErrors', () => {
  const el = (type: string, props: Record<string, unknown>, children?: DesignerElement[]): DesignerElement =>
    ({ id: type.toLowerCase().replace(/\s/g, '-'), type, properties: props, children })

  it('returns no errors when every input field has a valid pgType', () => {
    const root = el('Stack', {}, [
      el('Text Input', { fieldKey: 'name', pgType: 'varchar(80)' }),
      el('Number Input', { fieldKey: 'qty', pgType: 'numeric(10,2)' }),
      el('Label', { text: 'hi' }), // non-input ignored
    ])
    expect(collectPgTypeErrors(root)).toEqual([])
  })

  it('flags input fields with missing or invalid pgType', () => {
    const root = el('Stack', {}, [
      el('Text Input', { fieldKey: 'name' }), // missing
      el('Number Input', { fieldKey: 'qty', pgType: 'numeric' }), // incomplete
    ])
    expect(collectPgTypeErrors(root)).toHaveLength(2)
  })

  it('ignores non-pgType-bearing components', () => {
    const root = el('Stack', {}, [el('Label', { text: 'x' }), el('Button', { label: 'ok' })])
    expect(collectPgTypeErrors(root)).toEqual([])
  })
})

describe('DEFAULT_PGTYPE_BY_TYPE', () => {
  it('has sensible defaults per input component', () => {
    expect(DEFAULT_PGTYPE_BY_TYPE['Number Input']).toBe('numeric(18,2)')
    expect(DEFAULT_PGTYPE_BY_TYPE['Checkbox']).toBe('boolean')
    expect(DEFAULT_PGTYPE_BY_TYPE['DateTime Picker']).toBe('timestamptz')
    expect(DEFAULT_PGTYPE_BY_TYPE['Text Input']).toBe('text')
  })
})
