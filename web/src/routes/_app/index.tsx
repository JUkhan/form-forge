import { createFileRoute } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'

export const Route = createFileRoute('/_app/')({
  component: HomePage,
})

function HomePage() {
  const { t } = useTranslation()
  return <h1>{t('app.home', 'FormForge')}</h1>
}
