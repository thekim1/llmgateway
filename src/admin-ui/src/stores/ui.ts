import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { DEFAULT_LOCALE, isAppLocale, setLocale as applyLocale, type AppLocale } from '@/i18n'
import {
  applyTheme,
  isThemePreference,
  readSystemPreferences,
  resolveTheme,
  type SystemPreferences,
  type ThemeId,
  type ThemePreference,
} from '@/theme/themes'

/** Only non-sensitive display preferences are persisted. */
export const THEME_STORAGE_KEY = 'ume-admin.theme'
export const LOCALE_STORAGE_KEY = 'ume-admin.locale'

export type ToastKind = 'success' | 'info' | 'warning' | 'error'
export interface Toast {
  id: number
  kind: ToastKind
  message: string
}

/** Maximum number of visible toasts. Errors are never removed automatically. */
const MAX_TOASTS = 4

function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value)
  } catch {
    // Storage may be unavailable (private mode) – preferences then last for the session only.
  }
}

export const useUiStore = defineStore('ui', () => {
  const storedTheme = readStorage(THEME_STORAGE_KEY)
  const storedLocale = readStorage(LOCALE_STORAGE_KEY)

  const themePreference = ref<ThemePreference>(isThemePreference(storedTheme) ? storedTheme : 'system')
  const systemPreferences = ref<SystemPreferences>(readSystemPreferences())
  const theme = computed<ThemeId>(() => resolveTheme(themePreference.value, systemPreferences.value))
  const locale = ref<AppLocale>(isAppLocale(storedLocale) ? storedLocale : DEFAULT_LOCALE)

  const toasts = ref<Toast[]>([])
  let nextToastId = 1

  function setTheme(preference: ThemePreference): void {
    themePreference.value = preference
    writeStorage(THEME_STORAGE_KEY, preference)
    applyTheme(theme.value)
  }

  function setLocale(next: AppLocale): void {
    locale.value = next
    writeStorage(LOCALE_STORAGE_KEY, next)
    applyLocale(next)
  }

  let mediaListenersAttached = false
  /** Applies the current preferences and follows OS changes when the preference is `system`. */
  function init(): void {
    applyTheme(theme.value)
    applyLocale(locale.value)
    if (mediaListenersAttached || typeof window === 'undefined' || !window.matchMedia) return
    mediaListenersAttached = true
    const update = () => {
      systemPreferences.value = readSystemPreferences()
      applyTheme(theme.value)
    }
    for (const query of ['(prefers-color-scheme: dark)', '(prefers-contrast: more)']) {
      window.matchMedia(query).addEventListener?.('change', update)
    }
  }

  function notify(kind: ToastKind, message: string): number {
    const id = nextToastId++
    const next = [...toasts.value, { id, kind, message }]
    while (next.length > MAX_TOASTS) {
      const removable = next.findIndex((t) => t.kind !== 'error')
      if (removable === -1) break
      next.splice(removable, 1)
    }
    toasts.value = next
    return id
  }

  function dismiss(id: number): void {
    toasts.value = toasts.value.filter((t) => t.id !== id)
  }

  function dismissAll(): void {
    toasts.value = []
  }

  return {
    themePreference,
    theme,
    locale,
    toasts,
    setTheme,
    setLocale,
    init,
    notify,
    dismiss,
    dismissAll,
  }
})
