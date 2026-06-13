export class ApiError extends Error {
  public readonly status: number
  public readonly code: string
  public readonly messageKey: string
  public readonly detail?: string
  public readonly fieldErrors?: Record<string, string[]>
  public readonly correlationId?: string

  constructor(
    status: number,
    code: string,
    messageKey: string,
    detail?: string,
    fieldErrors?: Record<string, string[]>,
    correlationId?: string,
  ) {
    super(`API error ${status}: ${code}`)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.messageKey = messageKey
    this.detail = detail
    this.fieldErrors = fieldErrors
    this.correlationId = correlationId
  }
}
