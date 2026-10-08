import { isApiError } from '@/api/client'

/** Server-provided messages (409 conflicts, validation) arrive in Swedish; local fallbacks are English. */
export function problemLang(error: unknown): 'sv' | undefined {
  if (!isApiError(error)) return undefined
  return error.problem?.detail || error.problem?.title ? 'sv' : undefined
}

export function problemMessage(error: unknown): string {
  if (!isApiError(error)) return 'Something went wrong. Try again.'
  if (error.status === 0) return "Can't reach the server. Check your connection and try again."
  const server = error.problem?.detail || error.problem?.title
  if (server) return server
  switch (error.status) {
    case 401:
      return 'Your session has expired. Sign in again.'
    case 403:
      return "You don't have permission to do that."
    case 404:
      return "That item doesn't exist any more. Refresh the page."
    case 409:
      return 'That conflicts with something that already exists.'
    case 429:
      return 'Too many requests. Wait a moment and try again.'
    default:
      return error.status >= 500 ? 'The server had a problem. Try again in a moment.' : 'The request was rejected. Check your input.'
  }
}

/** Maps ProblemDetails `errors` (any casing) onto form field names. First message per field. */
export function mapFieldErrors(error: unknown, fields: readonly string[]): Record<string, string> {
  const result: Record<string, string> = {}
  if (!isApiError(error)) return result
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const match = fields.find((f) => f.toLowerCase() === key.toLowerCase().replace(/^\$\./, ''))
    const message = messages[0]
    if (match && message) result[match] = message
  }
  return result
}

/** Seconds from a 429 Retry-After value stored in the problem extensions, if any. */
export function retryAfterSeconds(error: unknown): number | null {
  if (!isApiError(error) || error.status !== 429) return null
  const value = error.problem?.retryAfter
  return typeof value === 'number' ? value : null
}
