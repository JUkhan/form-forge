import { describe, it, expect } from 'vitest'
import { extractPlaceholders, hasPlaceholders } from '../queryParameters'

describe('extractPlaceholders', () => {
  it('returns [] for empty/undefined/no-placeholder SQL', () => {
    expect(extractPlaceholders(undefined)).toEqual([])
    expect(extractPlaceholders('')).toEqual([])
    expect(extractPlaceholders('select * from t where a = 1')).toEqual([])
  })

  it('captures both bare {_x} and quoted \'{_x}\' forms, distinct, in first-seen order', () => {
    const sql =
      "select s.name from students s where s.age > {_age} AND s.id = '{_student_id}'"
    expect(extractPlaceholders(sql)).toEqual(['_age', '_student_id'])
  })

  it('dedupes a repeated placeholder', () => {
    expect(extractPlaceholders('a = {_x} or b = {_x} or c = {_y}')).toEqual(['_x', '_y'])
  })
})

describe('hasPlaceholders', () => {
  it('is true only when a placeholder is present', () => {
    expect(hasPlaceholders('where a = {_x}')).toBe(true)
    expect(hasPlaceholders("where a = '{_x}'")).toBe(true)
    expect(hasPlaceholders('where a = 1')).toBe(false)
    expect(hasPlaceholders(null)).toBe(false)
    // The shared regex is stateful (global flag) — verify repeated calls stay correct.
    expect(hasPlaceholders('no params here')).toBe(false)
    expect(hasPlaceholders('has {_p}')).toBe(true)
  })
})
