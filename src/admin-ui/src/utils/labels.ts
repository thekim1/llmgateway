import type {
  BudgetPeriod,
  BudgetScope,
  CircuitState,
  DataResidency,
  HealthStatus,
  KeyStatus,
  ModelKind,
  PiiPolicy,
  ProviderCapability,
  RequestOutcome,
} from '@/api/types'

export type Tone = 'ok' | 'warn' | 'danger' | 'neutral' | 'accent'

export const KEY_STATUS: Record<KeyStatus, { label: string; tone: Tone }> = {
  Active: { label: 'Active', tone: 'ok' },
  InGracePeriod: { label: 'Grace period', tone: 'warn' },
  Expired: { label: 'Expired', tone: 'neutral' },
  Revoked: { label: 'Revoked', tone: 'danger' },
  Disabled: { label: 'Disabled', tone: 'neutral' },
}

export const HEALTH_STATUS: Record<HealthStatus, { label: string; tone: Tone }> = {
  Healthy: { label: 'Healthy', tone: 'ok' },
  Degraded: { label: 'Degraded', tone: 'warn' },
  Unhealthy: { label: 'Down', tone: 'danger' },
}

export const CIRCUIT_LABEL: Record<CircuitState, { label: string; tone: Tone }> = {
  Closed: { label: 'Healthy', tone: 'ok' },
  Open: { label: 'Circuit open', tone: 'danger' },
}

export const RESIDENCY: Record<DataResidency, { label: string; glyph: string; cls: string; description: string }> = {
  OnPrem: { label: 'On-prem', glyph: '■', cls: 'text-res-onprem', description: 'Runs in the municipality’s own data centre.' },
  Eu: { label: 'EU', glyph: '●', cls: 'text-res-eu', description: 'Hosted by a provider inside the EU/EEA.' },
  External: { label: 'External', glyph: '◆', cls: 'text-res-ext', description: 'Hosted outside the EU/EEA.' },
}

export const PII_LABEL: Record<PiiPolicy, string> = {
  Off: 'Off',
  Allow: 'Allow',
  Redact: 'Redact',
  Block: 'Block',
  RerouteToOnPrem: 'On-prem',
}

export const PII_HINT: Record<PiiPolicy, string> = {
  Off: 'No PII check. Use only for public data.',
  Allow: 'PII is detected and logged as a category, then sent on.',
  Redact: 'Detected PII is masked before it leaves the gateway.',
  Block: 'Requests containing PII are rejected with pii_blocked.',
  RerouteToOnPrem: 'Requests with PII are sent to on-prem models only.',
}

export const SCOPE_LABEL: Record<BudgetScope, string> = { Department: 'Department', Team: 'Team', VirtualKey: 'Key' }
export const PERIOD_LABEL: Record<BudgetPeriod, string> = {
  Daily: 'Daily',
  Weekly: 'Weekly',
  Monthly: 'Monthly',
  Quarterly: 'Quarterly',
  Yearly: 'Yearly',
}
export const KIND_LABEL: Record<ModelKind, string> = { Chat: 'Chat', Embedding: 'Embedding' }

export const PROVIDER_TYPE_LABEL: Record<import('@/api/types').ProviderType, string> = {
  OpenAI: 'OpenAI',
  AzureOpenAI: 'Azure OpenAI',
  AzureAIFoundry: 'Azure AI Foundry',
  Anthropic: 'Anthropic',
  Ollama: 'Ollama',
  OllamaCloud: 'Ollama Cloud',
  OpenAICompatible: 'OpenAI-compatible',
}

export const AUTH_MODE_LABEL: Record<import('@/api/types').ProviderAuthMode, string> = {
  None: 'No authentication',
  Bearer: 'Bearer token',
  ApiKeyHeader: 'API key header',
  XApiKeyHeader: 'X-API-Key header',
}

export const PARAMETER_PROFILE_LABEL: Record<import('@/api/types').ParameterProfile, string> = {
  Standard: 'Standard',
  OpenAIReasoning: 'OpenAI reasoning',
}

export const CAPABILITY_LABEL: Record<ProviderCapability, string> = {
  ChatCompletions: 'Chat completions',
  Embeddings: 'Embeddings',
  Responses: 'Responses',
  AnthropicMessages: 'Anthropic messages',
  Streaming: 'Streaming',
}

export const OUTCOME_LABEL: Record<RequestOutcome, { label: string; tone: Tone }> = {
  Success: { label: 'Success', tone: 'ok' },
  ProviderError: { label: 'Provider error', tone: 'danger' },
  BudgetExceeded: { label: 'Over budget', tone: 'danger' },
  RateLimited: { label: 'Rate limited', tone: 'warn' },
  PiiBlocked: { label: 'PII blocked', tone: 'warn' },
  Rejected: { label: 'Rejected', tone: 'warn' },
  ClientCancelled: { label: 'Cancelled', tone: 'neutral' },
}

export const TONE_CLASSES: Record<Tone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  neutral: 'bg-sunken text-fg-2',
  accent: 'bg-accent-soft text-accent-ink',
}

export function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : pluralForm}`
}

/** `allowedModels: []` means every model; `allowedResidencies: []` means any residency. */
export function modelsLabel(models: readonly string[]): string {
  return models.length === 0 ? 'All models' : models.join(', ')
}

export function residenciesLabel(residencies: readonly DataResidency[]): string {
  return residencies.length === 0 ? 'Any residency' : residencies.map((r) => RESIDENCY[r].label).join(', ')
}
