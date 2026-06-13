// Client-side dataset_name validation — mirrors DatasetName.cs rules (AR-57 / FR-57).
// Consumed by Story 8.10's create/edit modal form (datasetNameSchema). Intentionally
// standalone (no React, no i18n hooks) so it imports cleanly into any form or test.
// The server is the authority; this provides fast inline feedback before any API call.
import { z } from 'zod'

const DATASET_NAME_REGEX = /^[a-z_][a-z0-9_]*$/

// Mirrors the permanent denylist in DatasetName.cs (AR-57).
export const DATASET_NAME_DENYLIST = new Set<string>([
  'users',
  'roles',
  'user_roles',
  'menus',
  'menu_role_assignments',
  'component_schemas',
  'refresh_tokens',
  'password_reset_tokens',
  'mfa_backup_codes',
  'mfa_sessions',
  'schema_audit_log',
  'mutation_audit_log',
  'dataset_audit_log',
  'custom_dataset',
])

// PG reserved keywords subset — client checks the most common ones for fast
// feedback. The server's PgReservedKeywords is the authoritative, complete list.
const PG_RESERVED_SUBSET = new Set<string>([
  'all', 'and', 'any', 'as', 'asc', 'authorization', 'binary', 'both',
  'case', 'cast', 'check', 'collate', 'column', 'constraint', 'create',
  'cross', 'default', 'deferrable', 'desc', 'distinct', 'do', 'else',
  'end', 'except', 'false', 'fetch', 'for', 'foreign', 'from', 'full',
  'grant', 'group', 'having', 'in', 'inner', 'into', 'is', 'join',
  'lateral', 'left', 'like', 'limit', 'natural', 'not', 'null', 'offset',
  'on', 'only', 'or', 'order', 'outer', 'primary', 'references', 'right',
  'role', 'select', 'similar', 'some', 'symmetric', 'table', 'then', 'to',
  'true', 'union', 'unique', 'user', 'using', 'variadic', 'when',
  'where', 'window', 'with',
])

export { DATASET_NAME_REGEX }

export const datasetNameSchema = z
  .string()
  .min(1, 'Dataset name is required.')
  .max(63, 'Dataset name must be 63 characters or fewer.')
  .regex(
    DATASET_NAME_REGEX,
    'Use lowercase letters (a-z), digits (0-9), and underscores (_). Must start with a letter or underscore.',
  )
  .refine((name) => !name.startsWith('pg_'), {
    message: "Names starting with 'pg_' are reserved by PostgreSQL.",
  })
  .refine((name) => !PG_RESERVED_SUBSET.has(name), {
    message: 'This is a reserved PostgreSQL keyword.',
  })
  .refine((name) => !DATASET_NAME_DENYLIST.has(name), {
    message: 'This name is reserved for internal use.',
  })
