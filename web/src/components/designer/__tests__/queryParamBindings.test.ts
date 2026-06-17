import { describe, it, expect } from 'vitest'
import {
  addQueryParamBinding,
  parseQueryParamBindings,
  removeQueryParamBinding,
  resolveQueryParameters,
  unmappedParams,
  updateQueryParamBinding,
  type QueryParamBinding,
} from '../queryParamBindings'

function bindings(items: Array<Partial<QueryParamBinding>>): QueryParamBinding[] {
  return items.map((b, i) => ({
    id: b.id ?? `b${i}`,
    param: b.param ?? '',
    valueFieldKey: b.valueFieldKey ?? '',
  }))
}

describe('parseQueryParamBindings', () => {
  it('returns [] for malformed input', () => {
    expect(parseQueryParamBindings(null)).toEqual([])
    expect(parseQueryParamBindings('nope')).toEqual([])
    expect(parseQueryParamBindings({})).toEqual([])
  })

  it('normalizes rows and fills missing fields', () => {
    const parsed = parseQueryParamBindings([
      { id: 'b1', param: '_age', valueFieldKey: 'age' },
      { param: '_name' },
    ])
    expect(parsed[0]).toMatchObject({ id: 'b1', param: '_age', valueFieldKey: 'age' })
    expect(parsed[1].param).toBe('_name')
    expect(parsed[1].valueFieldKey).toBe('')
    expect(parsed[1].id).not.toBe('')
  })
})

describe('editor ops', () => {
  it('adds (with optional default param), updates, and removes immutably', () => {
    let list = addQueryParamBinding([], '_age')
    expect(list).toHaveLength(1)
    expect(list[0].param).toBe('_age')
    const id = list[0].id
    list = updateQueryParamBinding(list, id, { valueFieldKey: 'age' })
    expect(list[0].valueFieldKey).toBe('age')
    list = removeQueryParamBinding(list, id)
    expect(list).toHaveLength(0)
  })
})

describe('unmappedParams', () => {
  it('lists placeholders without a bound value field', () => {
    const b = bindings([
      { param: '_age', valueFieldKey: 'age' },
      { param: '_name', valueFieldKey: '' }, // selected but no value field → still unmapped
    ])
    expect(unmappedParams(b, ['_age', '_name', '_city'])).toEqual(['_name', '_city'])
  })

  it('is empty when all placeholders are bound', () => {
    const b = bindings([
      { param: '_age', valueFieldKey: 'age' },
      { param: '_name', valueFieldKey: 'nm' },
    ])
    expect(unmappedParams(b, ['_age', '_name'])).toEqual([])
  })
})

describe('resolveQueryParameters', () => {
  it('returns undefined when no placeholders', () => {
    expect(resolveQueryParameters([], [], {})).toBeUndefined()
  })

  it('returns undefined when a placeholder is unbound or its value is empty', () => {
    const b = bindings([{ param: '_age', valueFieldKey: 'age' }])
    expect(resolveQueryParameters(b, ['_age', '_name'], { age: '23' })).toBeUndefined() // _name unbound
    expect(resolveQueryParameters(b, ['_age'], {})).toBeUndefined() // value missing
    expect(resolveQueryParameters(b, ['_age'], { age: '' })).toBeUndefined() // empty value
  })

  it('builds the JSON object string from bound field values', () => {
    const b = bindings([
      { param: '_age', valueFieldKey: 'age' },
      { param: '_name', valueFieldKey: 'nm' },
    ])
    const json = resolveQueryParameters(b, ['_age', '_name'], { age: 23, nm: 'Carol' })
    expect(json).toBe(JSON.stringify({ _age: '23', _name: 'Carol' }))
  })
})
