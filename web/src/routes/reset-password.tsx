import { createFileRoute, Link, redirect, useNavigate } from '@tanstack/react-router'
import { useMemo } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { KeyRound, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useResetPasswordMutation } from '../features/auth/authMutations'
import { ApiError } from '../lib/api/apiError'

const resetSearchSchema = z.object({
  token: z.string().optional(),
})

// Unauthenticated route. The token arrives via ?token=<raw>; a missing token
// means the link was malformed — bounce to /forgot-password to request a new one.
export const Route = createFileRoute('/reset-password')({
  validateSearch: resetSearchSchema,
  beforeLoad: ({ search }) => {
    if (!search.token) {
      throw redirect({ to: '/forgot-password' })
    }
  },
  component: ResetPasswordPage,
})

// File-based routing requires the Route export alongside the component, which trips
// the fast-refresh rule; suppress to keep the baseline lint count unchanged.
// eslint-disable-next-line react-refresh/only-export-components
function ResetPasswordPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { token } = Route.useSearch()
  const resetMutation = useResetPasswordMutation()

  // Memoized on t so the refine message updates on language change without
  // recreating the schema object on every render (which would reset isSubmitting).
  const resetSchema = useMemo(
    () =>
      z
        .object({
          newPassword: z.string().min(8).max(72),
          confirmPassword: z.string(),
        })
        .refine((data) => data.newPassword === data.confirmPassword, {
          message: t('auth.resetPassword.passwordMismatch'),
          path: ['confirmPassword'],
        }),
    [t],
  )
  type ResetFormValues = z.infer<typeof resetSchema>

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<ResetFormValues>({ resolver: zodResolver(resetSchema) })

  const onSubmit = async (values: ResetFormValues) => {
    if (!token) {
      void navigate({ to: '/forgot-password', replace: true })
      return
    }
    try {
      await resetMutation.mutateAsync({
        token,
        newPassword: values.newPassword,
      })
      toast.success(t('auth.resetPassword.successToast'))
      void navigate({ to: '/login', replace: true })
    } catch (err) {
      if (err instanceof ApiError && err.code === 'RESET_TOKEN_INVALID') {
        // Dedicated invalid-token banner with a path back to request a fresh link.
        setError('root', { type: 'tokenInvalid' })
        return
      }
      const message =
        err instanceof ApiError ? t(err.messageKey, err.messageKey) : t('auth.genericError')
      setError('root', { message })
    }
  }

  const tokenInvalid = errors.root?.type === 'tokenInvalid'

  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 flex flex-col items-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-sm">
            <KeyRound className="h-6 w-6" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-foreground">
            {t('auth.resetPassword.title')}
          </h1>
        </div>

        {tokenInvalid ? (
          <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
            >
              {t('auth.resetPassword.invalidTokenBanner')}
            </div>
            <Link
              to="/forgot-password"
              className="inline-block text-sm font-medium text-primary hover:underline"
            >
              {t('auth.resetPassword.requestNewLink')}
            </Link>
          </div>
        ) : (
          <form
            onSubmit={(e) => {
              void handleSubmit(onSubmit)(e)
            }}
            aria-label={t('auth.resetPassword.title')}
            className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
          >
            <div className="space-y-1.5">
              <Label htmlFor="newPassword">{t('auth.resetPassword.newPasswordLabel')}</Label>
              <Input
                id="newPassword"
                type="password"
                autoComplete="new-password"
                aria-describedby={errors.newPassword ? 'new-password-error' : undefined}
                aria-invalid={errors.newPassword ? true : undefined}
                {...register('newPassword')}
              />
              {errors.newPassword && (
                <p id="new-password-error" role="alert" className="text-xs text-destructive">
                  {errors.newPassword.message}
                </p>
              )}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="confirmPassword">
                {t('auth.resetPassword.confirmPasswordLabel')}
              </Label>
              <Input
                id="confirmPassword"
                type="password"
                autoComplete="new-password"
                aria-describedby={errors.confirmPassword ? 'confirm-password-error' : undefined}
                aria-invalid={errors.confirmPassword ? true : undefined}
                {...register('confirmPassword')}
              />
              {errors.confirmPassword && (
                <p id="confirm-password-error" role="alert" className="text-xs text-destructive">
                  {errors.confirmPassword.message}
                </p>
              )}
            </div>

            {errors.root && errors.root.message && (
              <div
                role="alert"
                className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
              >
                {errors.root.message}
              </div>
            )}

            <Button type="submit" disabled={isSubmitting} className="w-full">
              {isSubmitting ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  {t('auth.resetPassword.submitting')}
                </>
              ) : (
                t('auth.resetPassword.submitButton')
              )}
            </Button>

            <Link
              to="/login"
              className="block text-center text-sm font-medium text-primary hover:underline"
            >
              {t('auth.resetPassword.backToLogin')}
            </Link>
          </form>
        )}
      </div>
    </div>
  )
}
