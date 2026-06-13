import { httpClient } from '../../features/auth/httpClient'
import { type Theme } from './themes'

export async function updateThemePreference(theme: Theme): Promise<void> {
  await httpClient.put('/api/users/me/preferences', { themePreference: theme })
}
