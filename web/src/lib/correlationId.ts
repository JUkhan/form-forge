// Generates a client-side correlation ID for X-Correlation-ID (Decision 3.8).
// 26 uppercase alphanumeric characters, derived from crypto.randomUUID. A
// spec-compliant ULID can be swapped in by installing the `ulid` npm package
// and replacing the body with `return ulid()`.
export function generateCorrelationId(): string {
  return crypto.randomUUID().replace(/-/g, '').toUpperCase().slice(0, 26)
}
