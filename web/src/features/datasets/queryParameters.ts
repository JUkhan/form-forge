// Parameterized-query feature — placeholder helpers shared by the admin dataset form
// (preview parameter prompt) and the designer Dataset palette (filterable validation).
// Mirrors the backend DatasetParameterResolver token grammar: a {_placeholder} optionally
// wrapped in single quotes ('{_id}'). The leading-underscore name is captured.

const PLACEHOLDER_RE = /'\{(_\w+)\}'|\{(_\w+)\}/g

// Distinct placeholder names ("_age", "_student_id", …) in first-seen order.
export function extractPlaceholders(sql: string | null | undefined): string[] {
  if (!sql) return []
  const seen = new Set<string>()
  const names: string[] = []
  for (const match of sql.matchAll(PLACEHOLDER_RE)) {
    const name = match[1] ?? match[2]
    if (name && !seen.has(name)) {
      seen.add(name)
      names.push(name)
    }
  }
  return names
}

export function hasPlaceholders(sql: string | null | undefined): boolean {
  PLACEHOLDER_RE.lastIndex = 0
  return PLACEHOLDER_RE.test(sql ?? '')
}
