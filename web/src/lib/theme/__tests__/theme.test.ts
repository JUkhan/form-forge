import { describe, it, expect, beforeEach } from 'vitest'
import { applyTheme, getStoredTheme } from '../applyTheme'
import { DEFAULT_THEME } from '../themes'

describe('applyTheme', () => {
  beforeEach(() => localStorage.clear())

  it('sets data-theme attribute on <html>', () => {
    applyTheme('nord')
    expect(document.documentElement.getAttribute('data-theme')).toBe('nord')
  })

  it('writes ff-theme to localStorage', () => {
    applyTheme('catppuccin')
    expect(localStorage.getItem('ff-theme')).toBe('catppuccin')
  })
})

describe('getStoredTheme', () => {
  beforeEach(() => localStorage.clear())

  it('returns DEFAULT_THEME when localStorage is empty', () => {
    expect(getStoredTheme()).toBe(DEFAULT_THEME)
  })

  it('returns stored valid theme', () => {
    localStorage.setItem('ff-theme', 'gruvbox')
    expect(getStoredTheme()).toBe('gruvbox')
  })

  it('returns DEFAULT_THEME for unknown stored value', () => {
    localStorage.setItem('ff-theme', 'hacker-green')
    expect(getStoredTheme()).toBe(DEFAULT_THEME)
  })
})
