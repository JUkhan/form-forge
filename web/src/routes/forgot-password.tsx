import { createFileRoute, Link } from '@tanstack/react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { KeyRound, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useForgotPasswordMutation } from '../features/auth/authMutations'

const forgotPasswordSchema = z.object({
  email: z.string().min(1).email().max(320),
})
type ForgotPasswordValues = z.infer<typeof forgotPasswordSchema>

// Unauthenticated route — no beforeLoad guard. A signed-in user can still reach
// this page; the backend handles it harmlessly (always returns the same 200).
export const Route = createFileRoute('/forgot-password')({
  component: ForgotPasswordPage,
})

// File-based routing requires the Route export alongside the component, which trips
// the fast-refresh rule; suppress to keep the baseline lint count unchanged.
// eslint-disable-next-line react-refresh/only-export-components
function ForgotPasswordPage() {
  const { t } = useTranslation()
  const forgotMutation = useForgotPasswordMutation()

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<ForgotPasswordValues>({ resolver: zodResolver(forgotPasswordSchema) })

  const onSubmit = async (values: ForgotPasswordValues) => {
    try {
      await forgotMutation.mutateAsync(values)
    } catch {
      // The endpoint is anti-enumeration (always 200), so a thrown error here is a
      // transport/server fault, not "email not found". Surface it inline.
      setError('root', { message: t('auth.genericError') })
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 flex flex-col items-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-sm">
            <KeyRound className="h-6 w-6" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-foreground">
            {t('auth.forgotPassword.title')}
          </h1>
        </div>

        {forgotMutation.isSuccess ? (
          <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
            <p role="status" className="text-sm text-foreground">
              {t('auth.forgotPassword.successMessage')}
            </p>
            <Link
              to="/login"
              className="inline-block text-sm font-medium text-primary hover:underline"
            >
              {t('auth.forgotPassword.backToLogin')}
            </Link>
          </div>
        ) : (
          <form
            onSubmit={(e) => {
              void handleSubmit(onSubmit)(e)
            }}
            aria-label={t('auth.forgotPassword.title')}
            className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
          >
            <p className="text-sm text-muted-foreground">
              {t('auth.forgotPassword.description')}
            </p>

            <div className="space-y-1.5">
              <Label htmlFor="email">{t('auth.forgotPassword.emailLabel')}</Label>
              <Input
                id="email"
                type="email"
                autoComplete="email"
                placeholder={t('auth.forgotPassword.emailPlaceholder')}
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
                  {t('auth.forgotPassword.submitting')}
                </>
              ) : (
                t('auth.forgotPassword.submitButton')
              )}
            </Button>

            <Link
              to="/login"
              className="block text-center text-sm font-medium text-primary hover:underline"
            >
              {t('auth.forgotPassword.backToLogin')}
            </Link>
          </form>
        )}
      </div>
    </div>
  )
}
