import type { RequestPolicy } from '@/api/types'

/** The request policy as the form edits it: numbers and JSON as text. */
export interface PolicyDraft {
  maxOutputTokens: string
  systemPrompt: string
  /** A JSON object of parameters, e.g. {"temperature": 0.2}. Empty = none. */
  defaults: string
  overrides: string
}

export type PolicyErrors = Partial<Record<keyof PolicyDraft, string>>

/** Parameters a policy may not set (the gateway refuses them too). */
export const PROTECTED_PARAMETERS = [
  'model', 'messages', 'input', 'prompt', 'system', 'instructions', 'stream', 'stream_options', 'file', 'image', 'mask',
  'max_tokens', 'max_completion_tokens', 'max_output_tokens',
  // Provider tools and storage need the key's own permission (Allow provider tools / storage), not a policy.
  'tools', 'tool_choice', 'web_search_options', 'mcp_servers', 'container', 'store', 'background', 'conversation',
  'previous_response_id',
]
export const MAX_SYSTEM_PROMPT = 8000
const MAX_PARAMETERS = 20

export const emptyPolicyDraft = (): PolicyDraft => ({ maxOutputTokens: '', systemPrompt: '', defaults: '', overrides: '' })

export function toPolicyDraft(policy: RequestPolicy | null | undefined): PolicyDraft {
  return {
    maxOutputTokens: policy?.maxOutputTokens?.toString() ?? '',
    systemPrompt: policy?.systemPrompt ?? '',
    defaults: policy?.defaults && Object.keys(policy.defaults).length > 0 ? JSON.stringify(policy.defaults, null, 2) : '',
    overrides: policy?.overrides && Object.keys(policy.overrides).length > 0 ? JSON.stringify(policy.overrides, null, 2) : '',
  }
}

function parseParameters(text: string): { value: Record<string, unknown> | null; error: string | null } {
  if (text.trim() === '') return { value: null, error: null }
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return { value: null, error: 'Enter a JSON object, for example {"temperature": 0.2}.' }
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { value: null, error: 'Enter a JSON object, for example {"temperature": 0.2}.' }
  }
  const names = Object.keys(parsed)
  if (names.length > MAX_PARAMETERS) return { value: null, error: `Set at most ${MAX_PARAMETERS} parameters.` }
  const blocked = names.find((n) => PROTECTED_PARAMETERS.includes(n))
  if (blocked) {
    return { value: null, error: blocked.startsWith('max_') ? `Use Max output tokens instead of '${blocked}'.` : `A policy cannot set '${blocked}'.` }
  }
  return { value: parsed as Record<string, unknown>, error: null }
}

/** The policy to send ({} clears it), or the problems with the draft. */
export function fromPolicyDraft(draft: PolicyDraft): { policy: RequestPolicy; errors: PolicyErrors } {
  const errors: PolicyErrors = {}
  let maxOutputTokens: number | null = null
  if (draft.maxOutputTokens.trim() !== '') {
    const n = Number(draft.maxOutputTokens.trim())
    if (!Number.isInteger(n) || n < 1 || n > 1_000_000) errors.maxOutputTokens = 'Enter a whole number from 1 to 1,000,000, or leave it empty.'
    else maxOutputTokens = n
  }
  if (draft.systemPrompt.length > MAX_SYSTEM_PROMPT) errors.systemPrompt = `The system prompt can be at most ${MAX_SYSTEM_PROMPT} characters.`
  const defaults = parseParameters(draft.defaults)
  if (defaults.error) errors.defaults = defaults.error
  const overrides = parseParameters(draft.overrides)
  if (overrides.error) errors.overrides = overrides.error

  const policy: RequestPolicy = {}
  if (maxOutputTokens !== null) policy.maxOutputTokens = maxOutputTokens
  if (draft.systemPrompt.trim() !== '') policy.systemPrompt = draft.systemPrompt.trim()
  if (defaults.value) policy.defaults = defaults.value
  if (overrides.value) policy.overrides = overrides.value
  return { policy, errors }
}
