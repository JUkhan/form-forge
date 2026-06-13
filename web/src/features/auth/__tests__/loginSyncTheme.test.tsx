import { cleanup, renderHook, act } from '@testing-library/react'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

vi.mock('../httpClient', () => ({
  httpClient: {
    post: vi.fn(),
  },
}))

vi.mock('../tokenStore', () => ({
  tokenStore: {
    set: vi.fn(),
    get: vi.fn(() => null),
    clear: vi.fn(),
  },
}))

vi.mock('@tanstack/react-router', () => ({
  useNavigate: () => vi.fn(),
}))

vi.mock('../../../lib/theme/applyTheme', () => ({
  applyTheme: vi.fn(),
}))

describe('useLoginMutation — theme sync on success', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('applies server themePreference to localStorage and data-theme after successful login', async () => {
    const { httpClient } = await import('../httpClient')
    const { applyTheme } = await import('../../../lib/theme/applyTheme')

    vi.mocked(httpClient.post).mockResolvedValueOnce({
      accessToken: 'test-access-token',
      refreshToken: 'test-refresh-token',
      expiresIn: 900,
      user: {
        userId: '00000000-0000-0000-0000-000000000001',
        email: 'user@example.com',
        displayName: 'Test User',
        themePreference: 'nord',
        roles: [],
      },
    })

    const { useLoginMutation } = await import('../authMutations')
    const { QueryClient, QueryClientProvider } = await import('@tanstack/react-query')
    const { createElement } = await import('react')

    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } })

    const wrapper = ({ children }: { children: React.ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children)

    const { result } = renderHook(() => useLoginMutation(), { wrapper })

    await act(async () => {
      await result.current.mutateAsync({ email: 'user@example.com', password: 'Password1!' })
    })

    expect(applyTheme).toHaveBeenCalledWith('nord')
  })

  it('does not call applyTheme when themePreference is null', async () => {
    const { httpClient } = await import('../httpClient')
    const { applyTheme } = await import('../../../lib/theme/applyTheme')

    vi.mocked(httpClient.post).mockResolvedValueOnce({
      accessToken: 'test-access-token',
      refreshToken: 'test-refresh-token',
      expiresIn: 900,
      user: {
        userId: '00000000-0000-0000-0000-000000000001',
        email: 'user@example.com',
        displayName: 'Test User',
        themePreference: null,
        roles: [],
      },
    })

    const { useLoginMutation } = await import('../authMutations')
    const { QueryClient, QueryClientProvider } = await import('@tanstack/react-query')
    const { createElement } = await import('react')

    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } })
    const wrapper = ({ children }: { children: React.ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children)

    const { result } = renderHook(() => useLoginMutation(), { wrapper })

    await act(async () => {
      await result.current.mutateAsync({ email: 'user@example.com', password: 'Password1!' })
    })

    expect(applyTheme).not.toHaveBeenCalled()
  })
})
