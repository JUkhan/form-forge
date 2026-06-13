import { THEMES, DEFAULT_THEME, type Theme } from './themes'

export function applyTheme(theme: Theme): void {
  if (typeof window === 'undefined') return
  document.documentElement.setAttribute('data-theme', theme)
  localStorage.setItem('ff-theme', theme)
}

export function getStoredTheme(): Theme {
  if (typeof window === 'undefined') return DEFAULT_THEME
  const stored = localStorage.getItem('ff-theme')
  return (THEMES as readonly string[]).includes(stored ?? '') ? (stored as Theme) : DEFAULT_THEME
}
