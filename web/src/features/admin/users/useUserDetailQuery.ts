import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { UserDetail } from './types'
import { USERS_QUERY_KEY } from './useUsersQuery'

export function useUserDetailQuery(userId: string) {
  return useQuery({
    queryKey: [...USERS_QUERY_KEY, userId] as const,
    queryFn: () => httpClient.get<UserDetail>(`/api/admin/users/${userId}`),
  })
}
