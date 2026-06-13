import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import type { ProvisioningStatus } from './types'

// Story 5.2 — side-effect-only companion to useMenuDetailQuery. The detail query
// itself owns the 2 s refetchInterval (driven by provisioningStatus === 'Pending'),
// so this hook only watches for transitions Pending → Success | Error and fires
// the corresponding sonner toast exactly once per transition.
//
// Why a ref-based "previous status" instead of a useEffect deps array on
// `provisioningStatus` alone: a deps array would also fire on the initial
// undefined → 'Pending' transition (which should NOT toast). We need the prior
// value to detect a true Pending → terminal flip.
export function usePollProvisioning(
  provisioningStatus: ProvisioningStatus | null | undefined,
  provisioningError: string | null | undefined,
) {
  const { t } = useTranslation()
  const previousStatus = useRef<ProvisioningStatus | null | undefined>(provisioningStatus)

  useEffect(() => {
    const prev = previousStatus.current
    if (prev === 'Pending' && provisioningStatus === 'Success') {
      toast.success(t('admin.menus.provisioningSuccess'))
    } else if (prev === 'Pending' && provisioningStatus === 'Error') {
      toast.error(
        t('admin.menus.provisioningError', {
          error: provisioningError ?? '',
        }),
      )
    }
    previousStatus.current = provisioningStatus
  }, [provisioningStatus, provisioningError, t])
}
