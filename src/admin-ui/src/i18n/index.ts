import { createI18n } from 'vue-i18n'
import sv from './locales/sv'
import en from './locales/en'

export const LOCALES = ['sv', 'en'] as const
export type AppLocale = (typeof LOCALES)[number]
export const DEFAULT_LOCALE: AppLocale = 'sv'

export type MessageSchema = typeof sv

export function isAppLocale(value: unknown): value is AppLocale {
  return typeof value === 'string' && (LOCALES as readonly string[]).includes(value)
}

export function createAppI18n(locale: AppLocale = DEFAULT_LOCALE) {
  return createI18n<[MessageSchema], AppLocale, false>({
    legacy: false,
    locale,
    fallbackLocale: DEFAULT_LOCALE,
    messages: { sv, en },
    missingWarn: false,
    fallbackWarn: false,
  })
}

export const i18n = createAppI18n()

export function setLocale(locale: AppLocale): void {
  i18n.global.locale.value = locale
  if (typeof document !== 'undefined') document.documentElement.lang = locale
}
