import { useTranslation } from 'react-i18next'
import { Input } from '@/components/ui/input'
import { Combobox, type ComboboxOption } from '@/components/ui/combobox'
import {
  PG_TYPE_OPTIONS,
  parsePgType,
  serializePgType,
  pgTypeNeedsLength,
  pgTypeNeedsPrecision,
  validatePgType,
  type ParsedPgType,
} from './pgTypes'

// Compact sizing matching the rest of the inspector (see INPUT_COMPACT_CLASS in
// PropertyInspector.tsx).
const INPUT_COMPACT_CLASS = 'h-7 text-xs'

interface PgTypeFieldProps {
  id: string
  value: string
  updateProp: (id: string, key: string, value: unknown) => void
}

function toInt(raw: string): number | undefined {
  if (raw.trim() === '') return undefined
  const n = Number(raw)
  return Number.isFinite(n) ? Math.trunc(n) : undefined
}

// Story B — the "PgTypeComponent": a searchable type picker plus the extra inputs
// a parametrised type needs (precision+scale for numeric, length for varchar/char).
// Binds the canonical pgType string (e.g. "numeric(10,2)") to the `pgType` property.
// Required: an empty or incomplete value surfaces inline and blocks save (see
// collectPgTypeErrors).
export function PgTypeField({ id, value, updateProp }: PgTypeFieldProps) {
  const { t } = useTranslation()
  const parsed = parsePgType(value)
  const options: ComboboxOption[] = PG_TYPE_OPTIONS.map((o) => ({
    value: o.base,
    label: o.label,
  }))

  function emit(next: ParsedPgType) {
    updateProp(id, 'pgType', serializePgType(next))
  }

  // Selecting a parametrised base pre-fills sensible defaults so the type is valid
  // immediately (the user can still adjust); switching away drops the params.
  function onBaseChange(base: string) {
    if (pgTypeNeedsPrecision(base)) {
      emit({ base, precision: parsed.precision ?? 18, scale: parsed.scale ?? 2 })
    } else if (pgTypeNeedsLength(base)) {
      emit({ base, length: parsed.length ?? 255 })
    } else {
      emit({ base })
    }
  }

  const invalid = validatePgType(value) !== null

  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-medium text-muted-foreground">
        {t('designer.inspector.fields.pgType')} *
      </span>
      <Combobox
        id={`pgtype-${id}`}
        value={parsed.base}
        onValueChange={onBaseChange}
        options={options}
        placeholder={t('designer.inspector.placeholders.pgType')}
        searchPlaceholder={t('designer.inspector.placeholders.pgTypeSearch')}
        className={cnInvalid(INPUT_COMPACT_CLASS, invalid)}
        aria-label={t('designer.inspector.fields.pgType')}
        aria-invalid={invalid ? true : undefined}
      />

      {pgTypeNeedsPrecision(parsed.base) && (
        <div className="flex gap-2">
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-[10px] text-muted-foreground">
              {t('designer.inspector.fields.pgTypePrecision')}
            </span>
            <Input
              type="number"
              min={1}
              max={1000}
              value={parsed.precision ?? ''}
              onChange={(e) =>
                emit({ base: parsed.base, precision: toInt(e.target.value), scale: parsed.scale })
              }
              className={INPUT_COMPACT_CLASS}
            />
          </label>
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-[10px] text-muted-foreground">
              {t('designer.inspector.fields.pgTypeScale')}
            </span>
            <Input
              type="number"
              min={0}
              value={parsed.scale ?? ''}
              onChange={(e) =>
                emit({ base: parsed.base, precision: parsed.precision, scale: toInt(e.target.value) })
              }
              className={INPUT_COMPACT_CLASS}
            />
          </label>
        </div>
      )}

      {pgTypeNeedsLength(parsed.base) && (
        <label className="flex flex-col gap-1">
          <span className="text-[10px] text-muted-foreground">
            {t('designer.inspector.fields.pgTypeLength')}
          </span>
          <Input
            type="number"
            min={1}
            value={parsed.length ?? ''}
            onChange={(e) => emit({ base: parsed.base, length: toInt(e.target.value) })}
            className={INPUT_COMPACT_CLASS}
          />
        </label>
      )}
    </div>
  )
}

// Local helper to avoid importing cn just for one conditional class — appends the
// destructive border ring when the type is incomplete/invalid.
function cnInvalid(base: string, invalid: boolean): string {
  return invalid ? `${base} border-destructive` : base
}
