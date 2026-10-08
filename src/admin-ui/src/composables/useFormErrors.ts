import { nextTick, reactive, ref } from 'vue'
import { mapFieldErrors, problemLang, problemMessage } from '@/utils/problem'

/**
 * Submit-time validation for forms. `fields` maps a field name to the DOM id of its control so the
 * first invalid field can receive focus. Server ProblemDetails errors are mapped by field name.
 */
export function useFormErrors<K extends string>(fieldIds: Record<K, string>) {
  const errors = reactive({}) as Partial<Record<K, string>>
  const formError = ref<string | null>(null)
  const formErrorLang = ref<'sv' | undefined>(undefined)

  function clear(): void {
    for (const key of Object.keys(errors)) delete errors[key as K]
    formError.value = null
    formErrorLang.value = undefined
  }

  function focusFirstInvalid(): void {
    void nextTick(() => {
      const first = (Object.keys(fieldIds) as K[]).find((k) => errors[k])
      if (first) document.getElementById(fieldIds[first])?.focus()
    })
  }

  /** Returns true when no client-side errors were set. */
  function validate(checks: Partial<Record<K, string | null | false | undefined>>): boolean {
    clear()
    for (const [key, message] of Object.entries(checks) as [K, string | null | false | undefined][]) {
      if (message) errors[key] = message
    }
    const ok = Object.keys(errors).length === 0
    if (!ok) focusFirstInvalid()
    return ok
  }

  function applyServerError(error: unknown): void {
    clear()
    const mapped = mapFieldErrors(error, Object.keys(fieldIds))
    for (const [key, message] of Object.entries(mapped)) errors[key as K] = message
    if (Object.keys(mapped).length > 0) focusFirstInvalid()
    else {
      formError.value = problemMessage(error)
      formErrorLang.value = problemLang(error)
    }
  }

  return { errors, formError, formErrorLang, clear, validate, applyServerError }
}
