import type { DesignerElement } from '@/types/designer'

// Story B — the PgType property model, shared by the PgType inspector component
// and the save-time validator. Mirrors the backend SafePgType allowlist + ranges
// (src/FormForge.Api/Features/SchemaRegistry/SafePgType.cs): keep the two in sync.

// PostgreSQL limits: char/varchar length 1..1 GB; numeric precision 1..1000,
// scale 0..precision.
export const MAX_STRING_LENGTH = 10_485_760
export const MAX_NUMERIC_PRECISION = 1000

// Bases that require extra inputs before the type is complete.
const LENGTH_BASES = new Set(['varchar', 'char'])
const PRECISION_BASES = new Set(['numeric'])

export function pgTypeNeedsLength(base: string): boolean {
  return LENGTH_BASES.has(base)
}
export function pgTypeNeedsPrecision(base: string): boolean {
  return PRECISION_BASES.has(base)
}

// The combobox option list. `base` is the canonical token stored in the value;
// `label` is the human-facing name. Order roughly by frequency of use.
export interface PgTypeOption {
  base: string
  label: string
}
export const PG_TYPE_OPTIONS: readonly PgTypeOption[] = [
  { base: 'text', label: 'text' },
  { base: 'varchar', label: 'varchar(n)' },
  { base: 'char', label: 'char(n)' },
  { base: 'numeric', label: 'numeric(p,s)' },
  { base: 'integer', label: 'integer' },
  { base: 'bigint', label: 'bigint' },
  { base: 'smallint', label: 'smallint' },
  { base: 'boolean', label: 'boolean' },
  { base: 'uuid', label: 'uuid' },
  { base: 'date', label: 'date' },
  { base: 'time', label: 'time' },
  { base: 'timestamp', label: 'timestamp' },
  { base: 'timestamptz', label: 'timestamptz' },
  { base: 'double precision', label: 'double precision' },
  { base: 'real', label: 'real' },
  { base: 'jsonb', label: 'jsonb' },
]

const NO_PARAM_BASES = new Set([
  'text',
  'integer',
  'bigint',
  'smallint',
  'boolean',
  'uuid',
  'date',
  'time',
  'timestamp',
  'timestamptz',
  'double precision',
  'real',
  'jsonb',
])

// Default pgType per input component type. Keys match the element `type` strings
// the Designer canvas creates (see PropertyInspector switch / createNewElement).
// Dropdown's designer-source variant is forced to uuid by PropertyInspector when
// the user picks "Designer"; the static default is text.
export const DEFAULT_PGTYPE_BY_TYPE: Readonly<Record<string, string>> = {
  'Text Input': 'text',
  TextArea: 'text',
  'Number Input': 'numeric(18,2)',
  Checkbox: 'boolean',
  Dropdown: 'text',
  'DateTime Picker': 'timestamptz',
  'Color Picker': 'text',
  Image: 'text',
  File: 'text',
}

// Component types that must carry a valid pgType at save time. Matches the keys
// of DEFAULT_PGTYPE_BY_TYPE. (Repeater is input-bearing for fieldKey purposes but
// produces no column of its own, so it has no pgType.)
export const PGTYPE_BEARING_TYPES: ReadonlySet<string> = new Set(
  Object.keys(DEFAULT_PGTYPE_BY_TYPE),
)

export interface ParsedPgType {
  base: string
  length?: number
  precision?: number
  scale?: number
}

// Parses a canonical pgType string into its parts. Tolerant: an unrecognised or
// empty value yields { base: '' } so the inspector shows the placeholder.
export function parsePgType(value: string | undefined | null): ParsedPgType {
  if (!value) return { base: '' }
  const s = value.trim().toLowerCase()

  const numeric = /^numeric\((\d+),(\d+)\)$/.exec(s)
  if (numeric) {
    return { base: 'numeric', precision: Number(numeric[1]), scale: Number(numeric[2]) }
  }
  const numericP = /^numeric\((\d+)\)$/.exec(s)
  if (numericP) return { base: 'numeric', precision: Number(numericP[1]) }

  const length = /^(varchar|char)\((\d+)\)$/.exec(s)
  if (length) return { base: length[1], length: Number(length[2]) }

  // Bare base (incl. an in-progress "numeric"/"varchar" with no params yet).
  return { base: s }
}

// Serialises parts back to the canonical string. Incomplete parametrised types
// (e.g. numeric with no precision) serialise to the bare base, which validatePgType
// then reports as invalid — so the save banner can guide the user.
export function serializePgType(parts: ParsedPgType): string {
  const { base } = parts
  if (pgTypeNeedsPrecision(base)) {
    if (parts.precision == null) return base
    return parts.scale == null
      ? `numeric(${parts.precision})`
      : `numeric(${parts.precision},${parts.scale})`
  }
  if (pgTypeNeedsLength(base)) {
    return parts.length == null ? base : `${base}(${parts.length})`
  }
  return base
}

// Validates a pgType string. Returns null when valid, else a human-readable
// reason. Mirrors backend SafePgType.TryCreate.
export function validatePgType(value: string | undefined | null): string | null {
  if (!value || value.trim() === '') return 'is required'
  const parsed = parsePgType(value)
  const { base } = parsed

  if (pgTypeNeedsPrecision(base)) {
    if (parsed.precision == null) return 'numeric requires a precision'
    if (parsed.precision < 1 || parsed.precision > MAX_NUMERIC_PRECISION)
      return `numeric precision must be between 1 and ${MAX_NUMERIC_PRECISION}`
    if (parsed.scale == null) return 'numeric requires a scale'
    if (parsed.scale < 0 || parsed.scale > parsed.precision)
      return 'numeric scale must be between 0 and the precision'
    return null
  }
  if (pgTypeNeedsLength(base)) {
    if (parsed.length == null) return `${base} requires a length`
    if (parsed.length < 1 || parsed.length > MAX_STRING_LENGTH)
      return `${base} length must be between 1 and ${MAX_STRING_LENGTH}`
    return null
  }
  if (NO_PARAM_BASES.has(base)) return null
  return `'${value}' is not a supported PostgreSQL type`
}

// Walks the tree and returns one error per input field whose pgType is missing or
// invalid. Empty array = save-ready (for the pgType dimension). Mirrors the shape
// of collectFieldKeyErrors so the two lists merge into one save-blocking banner.
export function collectPgTypeErrors(root: DesignerElement): string[] {
  const errors: string[] = []
  const MAX_DEPTH = 64

  function walk(el: DesignerElement, depth: number) {
    if (depth >= MAX_DEPTH) return
    if (PGTYPE_BEARING_TYPES.has(el.type)) {
      const raw = el.properties?.pgType
      const value = typeof raw === 'string' ? raw : ''
      const problem = validatePgType(value)
      if (problem !== null) {
        errors.push(`${el.type} (id: ${el.id}) has an invalid type: ${problem}.`)
      }
    }
    for (const child of el.children ?? []) walk(child, depth + 1)
  }

  walk(root, 0)
  return errors
}
