import { cleanup, render, screen, act } from '@testing-library/react'
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { ThemeProvider, useTheme } from '../ThemeProvider'
import { DEFAULT_THEME } from '../themes'

vi.mock('../preferencesApi', () => ({
  updateThemePreference: vi.fn(() => Promise.resolve()),
}))

function ThemeDisplay() {
  const { theme, setTheme } = useTheme()
  return (
    <>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme('catppuccin')}>Switch</button>
    </>
  )
}

describe('ThemeProvider', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('applies DEFAULT_THEME on first render when localStorage is empty', () => {
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    expect(screen.getByTestId('theme').textContent).toBe(DEFAULT_THEME)
    expect(document.documentElement.getAttribute('data-theme')).toBe(DEFAULT_THEME)
  })

  it('restores stored theme from localStorage on mount', () => {
    localStorage.setItem('ff-theme', 'gruvbox')
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    expect(screen.getByTestId('theme').textContent).toBe('gruvbox')
    expect(document.documentElement.getAttribute('data-theme')).toBe('gruvbox')
  })

  it('setTheme updates data-theme attribute and localStorage', async () => {
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    await act(async () => screen.getByRole('button').click())
    expect(document.documentElement.getAttribute('data-theme')).toBe('catppuccin')
    expect(localStorage.getItem('ff-theme')).toBe('catppuccin')
  })

  it('setTheme calls updateThemePreference with the new theme', async () => {
    const { updateThemePreference } = await import('../preferencesApi')
    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    await act(async () => screen.getByRole('button').click())
    expect(updateThemePreference).toHaveBeenCalledWith('catppuccin')
  })

  it('setTheme does not throw when updateThemePreference fails', async () => {
    const { updateThemePreference } = await import('../preferencesApi')
    vi.mocked(updateThemePreference).mockRejectedValueOnce(new Error('network error'))
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})

    render(<ThemeProvider><ThemeDisplay /></ThemeProvider>)
    // Should not throw
    await act(async () => screen.getByRole('button').click())

    // Theme still applied locally
    expect(document.documentElement.getAttribute('data-theme')).toBe('catppuccin')

    // Wait for the rejected promise to settle and console.warn to be called
    await act(async () => {})
    expect(warnSpy).toHaveBeenCalledWith(
      '[theme] failed to persist preference to server',
      expect.any(Error),
    )
    warnSpy.mockRestore()
  })
})
