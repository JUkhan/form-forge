import { useTranslation } from 'react-i18next'
import { ApiError } from '@/lib/api/apiError'

interface ErrorBannerProps {
  error: unknown
  onRetry?: () => void
}

export function ErrorBanner({ error, onRetry }: ErrorBannerProps) {
  const { t } = useTranslation()
  const message =
    error instanceof ApiError
      ? t(error.messageKey, error.detail ? { detail: error.detail } : {})
      : t('errors.genericError')
  const correlationId = error instanceof ApiError ? error.correlationId : undefined

  return (
    <div
      role="alert"
      className="rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-sm text-destructive"
    >
      <p className="font-medium">{message}</p>
      {correlationId && (
        <p className="mt-1 font-mono text-xs text-destructive">
          {t('errors.correlationId', { id: correlationId })}
        </p>
      )}
      {onRetry && (
        <button
          type="button"
          onClick={onRetry}
          className="mt-3 rounded bg-destructive/15 px-3 py-1 text-xs font-medium text-destructive transition-colors hover:bg-destructive/25"
        >
          {t('errors.retry')}
        </button>
      )}
    </div>
  )
}
