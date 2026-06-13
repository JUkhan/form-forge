import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './locales/en.json'

// Synchronous init before React renders (Decision 4.8 — initAsync: false,
// formerly initImmediate prior to i18next v25).
void i18n
  .use(initReactI18next)
  .init({
    lng: 'en',
    fallbackLng: 'en',
    resources: { en: { translation: en } },
    initAsync: false,
    interpolation: { escapeValue: false },
  })

export default i18n
