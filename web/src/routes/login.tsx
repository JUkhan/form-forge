import { useState } from 'react'
import { createFileRoute, Link, redirect } from '@tanstack/react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { Loader2, LogIn } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useLoginMutation, useMfaLoginVerifyMutation } from '../features/auth/authMutations'
import { tokenStore } from '../features/auth/tokenStore'
import { ApiError } from '../lib/api/apiError'

const loginSchema = z.object({
  email: z.string().min(1).email(),
  password: z.string().min(1).max(128),
})
type LoginFormValues = z.infer<typeof loginSchema>

// `redirect` must be a same-origin relative path. Rejects absolute URLs
// (`https://evil.com/…`), protocol-relative URLs (`//evil.com`), and
// `javascript:`/`data:` schemes — i.e. anything that could leave our origin.
const loginSearchSchema = z.object({
  redirect: z
    .string()
    .regex(/^\/(?!\/)/, 'must be a same-origin relative path')
    .optional(),
})

export const Route = createFileRoute('/login')({
  validateSearch: loginSearchSchema,
  beforeLoad: () => {
    if (tokenStore.get()) {
      throw redirect({ to: '/' })
    }
  },
  component: LoginPage,
})

function LoginPage() {
  const { t } = useTranslation()
  const { redirect: redirectTo } = Route.useSearch()
  const [mfaSessionToken, setMfaSessionToken] = useState<string | null>(null)
  const [useBackupCode, setUseBackupCode] = useState(false)
  const [mfaCode, setMfaCode] = useState('')
  const [mfaError, setMfaError] = useState('')
  const loginMutation = useLoginMutation(redirectTo, (token) => setMfaSessionToken(token))
  const mfaVerifyMutation = useMfaLoginVerifyMutation(redirectTo)

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<LoginFormValues>({ resolver: zodResolver(loginSchema) })

  const onSubmit = async (values: LoginFormValues) => {
    try {
      await loginMutation.mutateAsync(values)
    } catch (err) {
      const message =
        err instanceof ApiError
          ? t(err.messageKey, err.messageKey)
          : t('auth.genericError')
      setError('root', { message })
    }
  }

  // Story 2.14 — submit the second factor (TOTP or backup code).
  const handleMfaVerify = async () => {
    setMfaError('')
    try {
      await mfaVerifyMutation.mutateAsync({ mfaSessionToken: mfaSessionToken!, code: mfaCode })
    } catch (err) {
      if (err instanceof ApiError && err.code === 'MFA_SESSION_INVALID') {
        // Session expired — reset to the password screen. Set the error on the
        // password form's root error (persists after the challenge screen unmounts);
        // setMfaError alone would be lost when mfaSessionToken becomes null.
        setMfaSessionToken(null)
        setMfaError('')
        setError('root', { message: t('auth.mfaChallenge.sessionExpired') })
      } else if (err instanceof ApiError && err.code === 'MFA_CODE_INVALID') {
        setMfaError(t('auth.mfaCodeInvalid'))
      } else {
        setMfaError(t('errors.genericError'))
      }
    }
  }

  // Story 2.14 — MFA challenge screen, shown after a password-step success when
  // the account has MFA enabled (mfaSessionToken is set by useLoginMutation).
  if (mfaSessionToken) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
        <div className="w-full max-w-md">
          <div className="mb-6 flex flex-col items-center">
            <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-sm">
              <LogIn className="h-6 w-6" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight text-foreground">
              {t('auth.mfaChallenge.title')}
            </h1>
          </div>

          <form
            onSubmit={(e) => { e.preventDefault(); void handleMfaVerify() }}
            className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
          >
            <div className="space-y-1.5">
              <Label htmlFor="mfa-code">
                {useBackupCode
                  ? t('auth.mfaChallenge.backupCodeLabel')
                  : t('auth.mfaChallenge.codeLabel')}
              </Label>
              <Input
                id="mfa-code"
                type="text"
                inputMode={useBackupCode ? undefined : 'numeric'}
                maxLength={useBackupCode ? 8 : 6}
                placeholder={
                  useBackupCode
                    ? t('auth.mfaChallenge.backupCodePlaceholder')
                    : t('auth.mfaChallenge.codePlaceholder')
                }
                value={mfaCode}
                onChange={(e) => {
                  const val = useBackupCode
                    ? e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8)
                    : e.target.value.replace(/\D/g, '').slice(0, 6)
                  setMfaCode(val)
                }}
                autoFocus
              />
              {mfaError && (
                <p role="alert" className="text-xs text-destructive">
                  {mfaError}
                </p>
              )}
            </div>

            <Button
              type="submit"
              className="w-full"
              disabled={
                mfaVerifyMutation.isPending ||
                (useBackupCode ? mfaCode.length !== 8 : mfaCode.length !== 6)
              }
            >
              {mfaVerifyMutation.isPending
                ? t('auth.mfaChallenge.submitting')
                : t('auth.mfaChallenge.submitButton')}
            </Button>

            <div className="flex flex-col items-center gap-2 text-sm">
              <button
                type="button"
                className="text-primary hover:underline"
                onClick={() => {
                  setUseBackupCode((prev) => !prev)
                  setMfaCode('')
                  setMfaError('')
                }}
              >
                {useBackupCode
                  ? t('auth.mfaChallenge.useTotpCode')
                  : t('auth.mfaChallenge.useBackupCode')}
              </button>
              <button
                type="button"
                className="text-muted-foreground hover:underline"
                onClick={() => {
                  setMfaSessionToken(null)
                  setMfaCode('')
                  setMfaError('')
                  setUseBackupCode(false)
                }}
              >
                {t('auth.mfaChallenge.backToSignIn')}
              </button>
            </div>
          </form>
        </div>
      </div>
    )
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 flex flex-col items-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-sm">
            <LogIn className="h-6 w-6" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-foreground">
            {t('auth.login.title')}
          </h1>
        </div>

        <form
          onSubmit={(e) => {
            void handleSubmit(onSubmit)(e)
          }}
          aria-label={t('auth.login.title')}
          className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
        >
          <div className="space-y-1.5">
            <Label htmlFor="email">{t('auth.login.emailLabel')}</Label>
            <Input
              id="email"
              type="email"
              autoComplete="email"
              placeholder={t('auth.login.emailPlaceholder')}
              aria-describedby={errors.email ? 'email-error' : undefined}
              aria-invalid={errors.email ? true : undefined}
              {...register('email')}
            />
            {errors.email && (
              <p id="email-error" role="alert" className="text-xs text-destructive">
                {errors.email.message}
              </p>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="password">{t('auth.login.passwordLabel')}</Label>
            <Input
              id="password"
              type="password"
              autoComplete="current-password"
              aria-describedby={errors.password ? 'password-error' : undefined}
              aria-invalid={errors.password ? true : undefined}
              {...register('password')}
            />
            {errors.password && (
              <p id="password-error" role="alert" className="text-xs text-destructive">
                {errors.password.message}
              </p>
            )}
            <div className="text-right">
              <Link
                to="/forgot-password"
                className="text-xs font-medium text-primary hover:underline"
              >
                {t('auth.login.forgotPasswordLink')}
              </Link>
            </div>
          </div>

          {errors.root && (
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
                {t('auth.login.submitting')}
              </>
            ) : (
              t('auth.login.submitButton')
            )}
          </Button>
        </form>
      </div>
    </div>
  )
}
