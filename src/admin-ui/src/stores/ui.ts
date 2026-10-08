import { ref } from 'vue'
import { defineStore } from 'pinia'

export const THEME_STORAGE_KEY = 'ume-theme'
export const THEMES = ['light', 'dark', 'lumen'] as const
export type Theme = (typeof THEMES)[number]

export interface Toast {
  id: number
  message: string
}

const TOAST_MS = 4000

export function isTheme(value: unknown): value is Theme {
  return typeof value === 'string' && (THEMES as readonly string[]).includes(value)
}

function readStoredTheme(): Theme | null {
  try {
    const value = window.localStorage.getItem(THEME_STORAGE_KEY)
    return isTheme(value) ? value : null
  } catch {
    return null
  }
}

function systemTheme(): Theme {
  return typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export const useUiStore = defineStore('ui', () => {
  const theme = ref<Theme>(readStoredTheme() ?? systemTheme())
  const toasts = ref<Toast[]>([])
  const timers = new Map<number, ReturnType<typeof setTimeout>>()
  let nextId = 1

  function apply(): void {
    document.documentElement.dataset.theme = theme.value
  }

  function setTheme(next: Theme): void {
    theme.value = next
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, next)
    } catch {
      // Storage may be unavailable; the choice then lasts for the session.
    }
    apply()
  }

  function init(): void {
    apply()
  }

  function dismiss(id: number): void {
    const timer = timers.get(id)
    if (timer) clearTimeout(timer)
    timers.delete(id)
    toasts.value = toasts.value.filter((t) => t.id !== id)
  }

  function schedule(id: number): void {
    timers.set(id, setTimeout(() => dismiss(id), TOAST_MS))
  }

  /** Success confirmations only. Errors are shown inline, never as toasts. */
  function notify(message: string): number {
    const id = nextId++
    toasts.value = [...toasts.value, { id, message }]
    schedule(id)
    return id
  }

  function pause(id: number): void {
    const timer = timers.get(id)
    if (timer) clearTimeout(timer)
    timers.delete(id)
  }

  function resume(id: number): void {
    if (!timers.has(id)) schedule(id)
  }

  return { theme, toasts, setTheme, init, notify, dismiss, pause, resume }
})
