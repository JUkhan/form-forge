export const THEMES = [
  'nord',
  'catppuccin',
  'gruvbox',
  'rose-pine-dawn',
  'catppuccin-latte',
] as const
export type Theme = (typeof THEMES)[number]
export const DEFAULT_THEME: Theme = 'nord'

export const THEME_LABELS: Record<Theme, string> = {
  nord: 'theme.nord',
  catppuccin: 'theme.catppuccin',
  gruvbox: 'theme.gruvbox',
  'rose-pine-dawn': 'theme.rosePineDawn',
  'catppuccin-latte': 'theme.catppuccinLatte',
}
