import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { ApiError } from '../../../lib/api/apiError'

// tokenStore is module-level in-memory state; spy on it so we can assert the
// refreshed access token is stored and inspect the Authorization wiring.
vi.mock('../tokenStore', () => {
  let token: string | null = 'stale-access-token'
  return {
    tokenStore: {
      get: vi.fn(() => token),
      set: vi.fn((t: string) => {
        token = t
      }),
      clear: vi.fn(() => {
        token = null
      }),
    },
  }
})

interface FakeResponseInit {
  status: number
  contentType?: string | null
  data?: unknown
}

function fakeResponse({ status, contentType = null, data }: FakeResponseInit): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: {
      get: (key: string) => (key.toLowerCase() === 'content-type' ? contentType : null),
    },
    json: async () => {
      if (data === undefined) throw new Error('no json body')
      return data
    },
    text: async () => (data === undefined ? '' : JSON.stringify(data)),
  } as unknown as Response
}

describe('httpClient — 401 silent refresh and retry', () => {
  const replaceMock = vi.fn()
  const originalLocation = window.location

  beforeEach(() => {
    vi.clearAllMocks()
    replaceMock.mockReset()
    // window.location.replace is non-configurable in jsdom; swap the whole
    // location object so refreshSession's hard-redirect is observable.
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...originalLocation, replace: replaceMock },
    })
  })

  afterEach(() => {
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: originalLocation,
    })
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('refreshes and retries once on a bare (empty-body) 401 — the ASP.NET JwtBearer challenge', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    // 1) protected GET → bare 401: empty body, no content-type (the auth challenge).
    fetchMock.mockResolvedValueOnce(fakeResponse({ status: 401 }))
    // 2) POST /api/auth/refresh → 200 with a fresh access token.
    fetchMock.mockResolvedValueOnce(
      fakeResponse({
        status: 200,
        contentType: 'application/json',
        data: { accessToken: 'fresh-access-token', refreshToken: 'r', expiresIn: 900 },
      }),
    )
    // 3) retried GET → 200 with the real payload.
    fetchMock.mockResolvedValueOnce(
      fakeResponse({ status: 200, contentType: 'application/json', data: { value: 42 } }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const { httpClient } = await import('../httpClient')
    const { tokenStore } = await import('../tokenStore')

    const result = await httpClient.get<{ value: number }>('/api/widgets')

    expect(result).toEqual({ value: 42 })
    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/auth/refresh')
    expect(tokenStore.set).toHaveBeenCalledWith('fresh-access-token')
    expect(replaceMock).not.toHaveBeenCalled()
  })

  it('does NOT refresh on a text/html 401 (proxy/WAF page) — surfaces it as an error', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValueOnce(fakeResponse({ status: 401, contentType: 'text/html' }))
    vi.stubGlobal('fetch', fetchMock)

    const { httpClient } = await import('../httpClient')

    await expect(httpClient.get('/api/widgets')).rejects.toMatchObject({ status: 401 })
    // No refresh attempt, no second call.
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(replaceMock).not.toHaveBeenCalled()
  })

  it('redirects to /login when the refresh itself fails (refresh token expired)', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    // protected GET → bare 401
    fetchMock.mockResolvedValueOnce(fakeResponse({ status: 401 }))
    // refresh → 401 (refresh token expired): refreshSession clears + redirects.
    fetchMock.mockResolvedValueOnce(fakeResponse({ status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    const { httpClient } = await import('../httpClient')
    const { tokenStore } = await import('../tokenStore')

    await expect(httpClient.get('/api/widgets')).rejects.toBeInstanceOf(ApiError)
    expect(tokenStore.clear).toHaveBeenCalled()
    expect(replaceMock).toHaveBeenCalledWith('/login')
  })
})
