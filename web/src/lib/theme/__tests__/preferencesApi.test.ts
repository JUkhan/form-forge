import { describe, it, expect, vi, beforeEach } from 'vitest'
import { updateThemePreference } from '../preferencesApi'

vi.mock('../../../features/auth/httpClient', () => ({
  httpClient: {
    put: vi.fn(() => Promise.resolve(undefined)),
  },
}))

describe('updateThemePreference', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('calls httpClient.put with the correct path and body', async () => {
    const { httpClient } = await import('../../../features/auth/httpClient')
    await updateThemePreference('nord')
    expect(httpClient.put).toHaveBeenCalledWith('/api/users/me/preferences', { themePreference: 'nord' })
  })

  it('calls httpClient.put for each valid theme', async () => {
    const { httpClient } = await import('../../../features/auth/httpClient')
    await updateThemePreference('catppuccin')
    expect(httpClient.put).toHaveBeenCalledWith('/api/users/me/preferences', { themePreference: 'catppuccin' })
    await updateThemePreference('gruvbox')
    expect(httpClient.put).toHaveBeenCalledWith('/api/users/me/preferences', { themePreference: 'gruvbox' })
  })

  it('propagates rejection from httpClient.put', async () => {
    const { httpClient } = await import('../../../features/auth/httpClient')
    vi.mocked(httpClient.put).mockRejectedValueOnce(new Error('network error'))
    await expect(updateThemePreference('nord')).rejects.toThrow('network error')
  })
})
