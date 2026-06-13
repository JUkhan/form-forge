// Story 8.3 (FR-57 AC-5) — client-side dataset_name validation mirrors the server
// rules in DatasetName.cs. These assertions intentionally parallel the backend
// DatasetNameValidatorTests so the two layers stay in lock-step.
import { describe, expect, it } from 'vitest'
import {
  DATASET_NAME_DENYLIST,
  DATASET_NAME_REGEX,
  datasetNameSchema,
} from '../validation'

describe('datasetNameSchema', () => {
  it.each(['my_dataset', '_private', 'report_2024', 'a', 'a'.padEnd(63, 'b')])(
    'accepts the valid name %j',
    (name) => {
      expect(datasetNameSchema.safeParse(name).success).toBe(true)
    },
  )

  it.each([
    '', // empty
    ' ', // whitespace
    'MyDataset', // uppercase
    '123abc', // starts with a digit
    'my-dataset', // hyphen
    'a b', // space
    'a'.padEnd(64, 'b'), // 64 chars — too long
  ])('rejects the invalid pattern/length %j', (name) => {
    expect(datasetNameSchema.safeParse(name).success).toBe(false)
  })

  it.each(['select', 'from', 'table', 'group', 'user', 'role', 'pg_foo'])(
    'rejects the reserved keyword %j',
    (name) => {
      expect(datasetNameSchema.safeParse(name).success).toBe(false)
    },
  )

  it.each([...DATASET_NAME_DENYLIST])('rejects the denylisted name %j', (name) => {
    expect(datasetNameSchema.safeParse(name).success).toBe(false)
  })

  it('exports the shared regex for reuse by form components', () => {
    expect(DATASET_NAME_REGEX.test('valid_name')).toBe(true)
    expect(DATASET_NAME_REGEX.test('Invalid')).toBe(false)
  })
})
