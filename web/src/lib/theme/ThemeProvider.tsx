import { createContext, useContext, useState, useEffect, type ReactNode } from 'react'
import { type Theme } from './themes'
import { applyTheme, getStoredTheme } from './applyTheme'
import { updateThemePreference } from './preferencesApi'

interface ThemeContextValue {
  theme: Theme
  setTheme: (t: Theme) => void
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(getStoredTheme)

  useEffect(() => {
    applyTheme(theme)
  }, [])

  function setTheme(t: Theme) {
    applyTheme(t)
    setThemeState(t)
    // Fire-and-forget: theme works locally even if the server PUT fails.
    // AC-1's failure modes (auth, validation) cannot happen here because the
    // THEMES tuple matches the server validator's allow-list.
    updateThemePreference(t).catch((err) => {
      console.warn('[theme] failed to persist preference to server', err)
    })
  }

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error('useTheme must be used within ThemeProvider')
  return ctx
}
