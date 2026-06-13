import { useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { useChangePasswordMutation } from '@/features/auth/authMutations'
import {
  useMfaStatusQuery,
  useMfaEnrolMutation,
  useMfaVerifyEnrolmentMutation,
  type MfaEnrolResponse,
} from '@/features/auth/mfaMutations'
import { ApiError } from '@/lib/api/apiError'

export const Route = createFileRoute('/_app/settings')({
  component: SettingsPage,
})

const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1),
    newPassword: z.string().min(8).max(72),
    confirmNewPassword: z.string(),
  })
  .refine((d) => d.newPassword === d.confirmNewPassword, {
    // Zod refine runs outside React hooks, so it can't call t(). Emit a sentinel
    // the component swaps for the translated string at render time — same pattern
    // as UPDATE_REQUIRED_KEY in admin/users.$userId.tsx.
    message: 'PASSWORDS_MISMATCH_SENTINEL',
    path: ['confirmNewPassword'],
  })

type ChangePasswordFormValues = z.infer<typeof changePasswordSchema>

const MISMATCH_SENTINEL = 'PASSWORDS_MISMATCH_SENTINEL'

// File-based routing requires the Route export alongside the component, which trips
// the fast-refresh rule; suppress to keep the baseline lint count unchanged.
// eslint-disable-next-line react-refresh/only-export-components
function SettingsPage() {
  const { t } = useTranslation()
  const mutation = useChangePasswordMutation()
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
    reset,
    setError,
  } = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordSchema),
    mode: 'onChange',
    defaultValues: { currentPassword: '', newPassword: '', confirmNewPassword: '' },
  })

  const submit = async (values: ChangePasswordFormValues) => {
    try {
      await mutation.mutateAsync({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      })
      toast.success(t('settings.changePassword.successToast'))
      reset()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'CURRENT_PASSWORD_INCORRECT') {
        setError('currentPassword', { message: t('auth.currentPasswordIncorrect') })
        return
      }
      if (err instanceof ApiError && err.status === 422 && err.fieldErrors?.newPassword) {
        setError('newPassword', { message: t('auth.passwordSameAsCurrent') })
        return
      }
      setError('root', { message: t('errors.genericError') })
    }
  }

  // Story 2.13 — TOTP MFA enrolment.
  const { data: mfaStatus } = useMfaStatusQuery()
  const mfaEnrolMutation = useMfaEnrolMutation()
  const mfaVerifyMutation = useMfaVerifyEnrolmentMutation()
  const [mfaModalOpen, setMfaModalOpen] = useState(false)
  const [mfaStep, setMfaStep] = useState<1 | 2 | 3>(1)
  const [enrolData, setEnrolData] = useState<MfaEnrolResponse | null>(null)
  const [mfaCode, setMfaCode] = useState('')
  const [mfaCodeError, setMfaCodeError] = useState('')
  const [backupCodesSaved, setBackupCodesSaved] = useState(false)

  const handleOpenMfaModal = async () => {
    setMfaStep(1)
    setMfaCode('')
    setMfaCodeError('')
    setBackupCodesSaved(false)
    try {
      const data = await mfaEnrolMutation.mutateAsync()
      if (!data) {
        toast.error(t('errors.genericError'))
        return
      }
      setEnrolData(data)
      setMfaModalOpen(true)
    } catch {
      toast.error(t('errors.genericError'))
    }
  }

  const handleMfaVerify = async () => {
    setMfaCodeError('')
    try {
      await mfaVerifyMutation.mutateAsync({ code: mfaCode })
      setMfaStep(3)
    } catch (err) {
      if (err instanceof ApiError && err.code === 'MFA_CODE_INVALID') {
        setMfaCodeError(t('auth.mfaCodeInvalid'))
      } else if (err instanceof ApiError && err.code === 'MFA_NO_PENDING_ENROLMENT') {
        setMfaCodeError(t('auth.mfaNoPendingEnrolment'))
      } else {
        setMfaCodeError(t('errors.genericError'))
      }
    }
  }

  const handleMfaModalClose = () => {
    setMfaModalOpen(false)
    setEnrolData(null)
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold tracking-tight">{t('settings.title')}</h1>

      <form
        onSubmit={(e) => {
          void handleSubmit(submit)(e)
        }}
        aria-label={t('settings.changePassword.sectionTitle')}
        className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
      >
        <h2 className="text-lg font-semibold text-foreground">
          {t('settings.changePassword.sectionTitle')}
        </h2>

        <div className="space-y-1.5">
          <Label htmlFor="current-password">
            {t('settings.changePassword.currentPasswordLabel')}
          </Label>
          <Input
            id="current-password"
            type="password"
            autoComplete="current-password"
            aria-invalid={errors.currentPassword ? true : undefined}
            {...register('currentPassword')}
          />
          {errors.currentPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.currentPassword.message}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="new-password">
            {t('settings.changePassword.newPasswordLabel')}
          </Label>
          <Input
            id="new-password"
            type="password"
            autoComplete="new-password"
            aria-invalid={errors.newPassword ? true : undefined}
            {...register('newPassword')}
          />
          {errors.newPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.newPassword.message}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="confirm-new-password">
            {t('settings.changePassword.confirmNewPasswordLabel')}
          </Label>
          <Input
            id="confirm-new-password"
            type="password"
            autoComplete="new-password"
            aria-invalid={errors.confirmNewPassword ? true : undefined}
            {...register('confirmNewPassword')}
          />
          {errors.confirmNewPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.confirmNewPassword.message === MISMATCH_SENTINEL
                ? t('settings.changePassword.passwordMismatch')
                : errors.confirmNewPassword.message}
            </p>
          )}
        </div>

        {errors.root && (
          <div
            role="alert"
            className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
          >
            {errors.root.message}
          </div>
        )}

        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting
            ? t('settings.changePassword.submitting')
            : t('settings.changePassword.submitButton')}
        </Button>
      </form>

      {/* Security section */}
      <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
        <h2 className="text-lg font-semibold text-foreground">
          {t('settings.security.sectionTitle')}
        </h2>
        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-muted-foreground">{t('settings.security.description')}</p>
            <p className="mt-1 text-sm font-medium">
              {mfaStatus?.mfaEnabled
                ? t('settings.security.mfaEnabled')
                : t('settings.security.mfaDisabled')}
            </p>
          </div>
          <Button
            variant="outline"
            onClick={() => {
              void handleOpenMfaModal()
            }}
            disabled={mfaEnrolMutation.isPending || mfaModalOpen}
          >
            {mfaStatus?.mfaEnabled
              ? t('settings.security.reenrolButton')
              : t('settings.security.enableButton')}
          </Button>
        </div>
      </div>

      {/* MFA Enrolment Modal */}
      <Dialog
        open={mfaModalOpen}
        onOpenChange={(open) => {
          if (!open && mfaStep !== 3) handleMfaModalClose()
        }}
      >
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>
              {mfaStep === 1 && t('settings.security.modal.step1Title')}
              {mfaStep === 2 && t('settings.security.modal.step2Title')}
              {mfaStep === 3 && t('settings.security.modal.step3Title')}
            </DialogTitle>
          </DialogHeader>

          {mfaStep === 1 && enrolData && (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">
                {t('settings.security.modal.step1Instructions')}
              </p>
              <div className="flex justify-center">
                <img
                  src={enrolData.qrCodeDataUrl}
                  alt={t('settings.security.modal.qrAlt')}
                  className="h-48 w-48 rounded-md border border-border"
                />
              </div>
              <div className="space-y-1">
                <p className="text-xs text-muted-foreground">
                  {t('settings.security.modal.manualSecretLabel')}
                </p>
                <code className="block break-all rounded bg-muted px-2 py-1 font-mono text-xs">
                  {enrolData.secret}
                </code>
              </div>
              <Button className="w-full" onClick={() => setMfaStep(2)}>
                {t('settings.security.modal.nextButton')}
              </Button>
            </div>
          )}

          {mfaStep === 2 && (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">
                {t('settings.security.modal.step2Instructions')}
              </p>
              <div className="space-y-1.5">
                <Label htmlFor="mfa-code">{t('settings.security.modal.codeLabel')}</Label>
                <Input
                  id="mfa-code"
                  type="text"
                  inputMode="numeric"
                  maxLength={6}
                  placeholder={t('settings.security.modal.codePlaceholder')}
                  value={mfaCode}
                  onChange={(e) => setMfaCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                  autoFocus
                />
                {mfaCodeError && (
                  <p role="alert" className="text-xs text-destructive">
                    {mfaCodeError}
                  </p>
                )}
              </div>
              <div className="flex gap-2">
                <Button variant="outline" className="flex-1" onClick={() => setMfaStep(1)}>
                  {t('settings.security.modal.backButton')}
                </Button>
                <Button
                  className="flex-1"
                  onClick={() => {
                    void handleMfaVerify()
                  }}
                  disabled={mfaCode.length !== 6 || mfaVerifyMutation.isPending}
                >
                  {mfaVerifyMutation.isPending
                    ? t('settings.security.modal.verifying')
                    : t('settings.security.modal.verifyButton')}
                </Button>
              </div>
            </div>
          )}

          {mfaStep === 3 && enrolData && (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">
                {t('settings.security.modal.step3Instructions')}
              </p>
              <div className="grid grid-cols-2 gap-2">
                {enrolData.backupCodes.map((code) => (
                  <code
                    key={code}
                    className="rounded bg-muted px-2 py-1 text-center font-mono text-sm"
                  >
                    {code}
                  </code>
                ))}
              </div>
              <p className="text-xs font-medium text-muted-foreground">
                {t('settings.security.modal.backupCodesNote')}
              </p>
              <div className="flex items-center gap-2">
                <Checkbox
                  id="backup-codes-saved"
                  checked={backupCodesSaved}
                  onCheckedChange={(v) => setBackupCodesSaved(v === true)}
                />
                <Label htmlFor="backup-codes-saved" className="cursor-pointer text-sm">
                  {t('settings.security.modal.confirmSavedLabel')}
                </Label>
              </div>
              <Button className="w-full" disabled={!backupCodesSaved} onClick={handleMfaModalClose}>
                {t('settings.security.modal.doneButton')}
              </Button>
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  )
}
