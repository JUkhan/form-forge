import { tokenStore } from './tokenStore'
import { generateCorrelationId } from '../../lib/correlationId'
import { ApiError } from '../../lib/api/apiError'
import { refreshSession } from './refreshCoordinator'

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

// All paths are relative (e.g. '/api/auth/login').
// In dev, Vite's proxy forwards /api/* to the backend.
// In production, the API serves the SPA (same origin).
async function request<T>(
  method: HttpMethod,
  path: string,
  body?: unknown,
  isRetry = false,
): Promise<T> {
  const correlationId = generateCorrelationId()
  const token = tokenStore.get()

  const headers: Record<string, string> = {
    'X-Correlation-ID': correlationId,
  }

  // FormData bodies must not have an explicit Content-Type — the browser sets
  // multipart/form-data with the correct boundary. (Story 4.3 icon upload.)
  const isFormData = body instanceof FormData

  // Only declare a JSON content type when a body is actually being sent.
  // Setting Content-Type on a body-less GET/DELETE forces an unnecessary
  // CORS preflight and is semantically wrong per the Fetch spec.
  if (body !== undefined && !isFormData) headers['Content-Type'] = 'application/json'

  if (token) headers['Authorization'] = `Bearer ${token}`

  const response = await fetch(path, {
    method,
    headers,
    credentials: 'include', // sends HttpOnly refresh_token cookie
    body: body === undefined ? undefined : isFormData ? (body as FormData) : JSON.stringify(body),
  })

  // On first 401, attempt a silent token refresh and retry once. Guards:
  //   - /api/auth/login: a 401 here means INVALID_CREDENTIALS, not session
  //     expiry. Triggering refresh would either rotate a logged-in user's
  //     session under them or mask the credentials error as SESSION_EXPIRED.
  //   - /api/auth/refresh: would self-loop; refreshSession() also fetches it.
  //   - text/html 401: a 401 from a proxy/WAF HTML error page (deploy,
  //     maintenance, misconfig) is infrastructure, not auth — don't sign the
  //     user out over it.
  // The common expired-access-token case is the bare ASP.NET JwtBearer
  // challenge, which returns an EMPTY body (no content-type) plus a
  // `WWW-Authenticate: Bearer` header — NOT application/problem+json (the
  // framework does not run the auth challenge through ProblemDetails). So we
  // must refresh on ANY non-HTML 401; requiring problem+json here silently
  // skipped the refresh and forced users to reload the page by hand.
  const contentType = response.headers.get('content-type') ?? ''
  if (
    response.status === 401 &&
    !isRetry &&
    path !== '/api/auth/login' &&
    path !== '/api/auth/refresh' &&
    // Story 2.14 — a 401 here means a wrong MFA code or an expired MFA session,
    // not access-token expiry; the refresh-and-retry loop must not fire.
    path !== '/api/auth/mfa/verify' &&
    !contentType.includes('text/html')
  ) {
    const refreshed = await refreshSession()
    if (refreshed) {
      tokenStore.set(refreshed.accessToken)
      return request<T>(method, path, body, true)
    }
    // AC-5: refresh failed (token truly expired/invalid) — hard redirect to
    // /login so the user exits the now-broken route. window.location.replace
    // resets the history stack and is the only navigation available from this
    // plain-TS module.
    tokenStore.clear()
    window.location.replace('/login')
    throw new ApiError(401, 'SESSION_EXPIRED', 'auth.sessionExpired')
  }

  if (!response.ok) {
    let code = 'UNKNOWN_ERROR'
    let messageKey = 'errors.genericError'
    let detail: string | undefined
    let fieldErrors: Record<string, string[]> | undefined
    let problemCorrelationId: string | undefined

    try {
      const problem = (await response.json()) as {
        code?: string
        messageKey?: string
        detail?: string
        errors?: Record<string, string[]>
        correlationId?: string
      }
      code = problem.code ?? code
      messageKey = problem.messageKey ?? messageKey
      detail = problem.detail
      fieldErrors = problem.errors
      problemCorrelationId = problem.correlationId
    } catch {
      // ignore JSON parse errors — fall back to defaults
    }

    // Prefer the correlationId echoed in the ProblemDetails body; fall back to
    // the value we sent on the request so support can always trace a failed
    // call even if the server omits it from the response envelope.
    throw new ApiError(
      response.status,
      code,
      messageKey,
      detail,
      fieldErrors,
      problemCorrelationId ?? correlationId,
    )
  }

  // 204 No Content / 205 Reset Content never carry a body.
  if (response.status === 204 || response.status === 205) return undefined as T

  // Some success responses carry no body — notably 202 Accepted from the async
  // provisioning endpoints (bind / retry-binding return Results.Accepted() with an
  // empty payload). Read the body as text first so an empty payload resolves to
  // undefined instead of throwing a JSON parse error — which would otherwise
  // surface as a false error toast even though the call succeeded.
  const text = await response.text()
  return (text.length > 0 ? JSON.parse(text) : undefined) as T
}

export const httpClient = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
  patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
  delete: <T>(path: string) => request<T>('DELETE', path),
}
