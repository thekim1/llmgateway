import { computed, nextTick, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { isApiError } from '@/api/client'
import { useUiStore } from '@/stores/ui'

/** Translates any error from the API client into a plain-language Swedish/English message. */
export function useErrorMessage() {
  const { t } = useI18n()
  return function errorMessage(error: unknown): string {
    if (isApiError(error)) {
      if (error.isNetworkError) return t('errors.network')
      if (error.status === 401) return t('errors.unauthorized')
      if (error.status === 403) return t('errors.forbiddenAction')
      if (error.status === 404) return t('errors.notFound')
      if (error.status === 409) return error.problem?.detail ?? t('errors.conflict')
      if (error.status >= 500) return t('errors.server')
      return error.problem?.detail ?? error.problem?.title ?? t('errors.generic')
    }
    return t('errors.generic')
  }
}

/** Shows a success toast or a persistent error toast. */
export function useNotify() {
  const ui = useUiStore()
  const errorMessage = useErrorMessage()
  return {
    success: (message: string) => ui.notify('success', message),
    info: (message: string) => ui.notify('info', message),
    error: (error: unknown) => ui.notify('error', errorMessage(error)),
    errorText: (message: string) => ui.notify('error', message),
  }
}

/** Normalises server field names like `$.Name`, `Targets[0].Weight` → `name`, `targets`. */
export function normaliseFieldName(field: string): string {
  const cleaned = field.replace(/^\$\.?/, '').split(/[.[]/)[0] ?? ''
  return cleaned.charAt(0).toLowerCase() + cleaned.slice(1)
}

/**
 * Form error state for accessible forms: inline errors per field (linked with aria-describedby
 * by FormField) and an error summary at the top that receives focus on submit.
 */
export function useForm<F extends string>(formId: string, fields: readonly F[]) {
  const errorMessage = useErrorMessage()
  const errors = reactive<Partial<Record<F, string>>>({}) as Partial<Record<F, string>>
  const general = ref<string[]>([])
  const submitted = ref(false)
  const busy = ref(false)

  const fieldId = (field: F) => `${formId}-${field}`
  const summaryId = `${formId}-errors`

  const summary = computed(() => {
    const items: { href: string | null; message: string }[] = []
    for (const field of fields) {
      const message = errors[field]
      if (message) items.push({ href: `#${fieldId(field)}`, message })
    }
    for (const message of general.value) items.push({ href: null, message })
    return items
  })
  const hasErrors = computed(() => summary.value.length > 0)

  function clear(): void {
    for (const field of fields) delete errors[field]
    general.value = []
    submitted.value = false
  }

  function setError(field: F, message: string): void {
    errors[field] = message
  }

  async function focusSummary(): Promise<void> {
    submitted.value = true
    await nextTick()
    document.getElementById(summaryId)?.focus()
  }

  /** Maps ProblemDetails field errors to fields; anything unknown becomes a general error. */
  function applyApiError(error: unknown): void {
    const fieldErrors = isApiError(error) ? error.fieldErrors : {}
    const entries = Object.entries(fieldErrors)
    if (entries.length === 0) {
      general.value = [errorMessage(error)]
      return
    }
    for (const [rawField, messages] of entries) {
      const name = normaliseFieldName(rawField) as F
      const message = messages.join(' ')
      if ((fields as readonly string[]).includes(name)) errors[name] = message
      else general.value = [...general.value, message]
    }
  }

  /**
   * Runs validation, then the submit action. Returns true on success.
   * `validate` should call setError for each problem.
   */
  async function submit(validate: () => void, action: () => Promise<void>): Promise<boolean> {
    clear()
    validate()
    if (hasErrors.value) {
      await focusSummary()
      return false
    }
    busy.value = true
    try {
      await action()
      return true
    } catch (error) {
      applyApiError(error)
      await focusSummary()
      return false
    } finally {
      busy.value = false
    }
  }

  return {
    errors,
    general,
    summary,
    hasErrors,
    submitted,
    busy,
    fieldId,
    summaryId,
    clear,
    setError,
    applyApiError,
    focusSummary,
    submit,
  }
}

export type FormApi = ReturnType<typeof useForm<string>>
