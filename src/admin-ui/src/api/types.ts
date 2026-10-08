/**
 * Types for the admin API. Mirrors docs/admin-api.md exactly (camelCase, enums as strings,
 * ISO-8601 timestamps, money in SEK unless the field name says Usd).
 */

// ---------- Enums ----------
export const DATA_RESIDENCIES = ['OnPrem', 'Eu', 'External'] as const
export type DataResidency = (typeof DATA_RESIDENCIES)[number]

export const PROVIDER_TYPES = [
  'OpenAI',
  'AzureOpenAI',
  'AzureAIFoundry',
  'Anthropic',
  'Ollama',
  'OllamaCloud',
  'OpenAICompatible',
] as const
export type ProviderType = (typeof PROVIDER_TYPES)[number]

export const PROVIDER_AUTH_MODES = ['None', 'Bearer', 'ApiKeyHeader', 'XApiKeyHeader'] as const
export type ProviderAuthMode = (typeof PROVIDER_AUTH_MODES)[number]

export const PROVIDER_CAPABILITIES = [
  'ChatCompletions',
  'Embeddings',
  'Responses',
  'AnthropicMessages',
  'Streaming',
] as const
export type ProviderCapability = (typeof PROVIDER_CAPABILITIES)[number]

export const MODEL_KINDS = ['Chat', 'Embedding'] as const
export type ModelKind = (typeof MODEL_KINDS)[number]

export const PARAMETER_PROFILES = ['Standard', 'OpenAIReasoning'] as const
export type ParameterProfile = (typeof PARAMETER_PROFILES)[number]

export const PII_POLICIES = ['Off', 'Allow', 'Redact', 'Block', 'RerouteToOnPrem'] as const
export type PiiPolicy = (typeof PII_POLICIES)[number]

export const BUDGET_SCOPES = ['Department', 'Team', 'VirtualKey'] as const
export type BudgetScope = (typeof BUDGET_SCOPES)[number]

export const BUDGET_PERIODS = ['Daily', 'Weekly', 'Monthly', 'Quarterly', 'Yearly'] as const
export type BudgetPeriod = (typeof BUDGET_PERIODS)[number]

export const KEY_ROTATION_MODES = ['RevokeImmediately', 'Grace24Hours'] as const
export type KeyRotationMode = (typeof KEY_ROTATION_MODES)[number]

export const KEY_STATUSES = ['Active', 'InGracePeriod', 'Expired', 'Revoked', 'Disabled'] as const
export type KeyStatus = (typeof KEY_STATUSES)[number]

export const REQUEST_OUTCOMES = [
  'Success',
  'ProviderError',
  'BudgetExceeded',
  'RateLimited',
  'PiiBlocked',
  'Rejected',
  'ClientCancelled',
] as const
export type RequestOutcome = (typeof REQUEST_OUTCOMES)[number]

export const CIRCUIT_STATES = ['Closed', 'Open'] as const
export type CircuitState = (typeof CIRCUIT_STATES)[number]

export const HEALTH_STATUSES = ['Healthy', 'Degraded', 'Unhealthy'] as const
export type HealthStatus = (typeof HEALTH_STATUSES)[number]

export const ROLES = ['gateway-admin', 'department-admin', 'viewer'] as const
export type Role = (typeof ROLES)[number]

/** ISO-8601 timestamp with offset. */
export type IsoDateTime = string

// ---------- Errors ----------
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
  [extension: string]: unknown
}

// ---------- BFF ----------
export interface BffUser {
  isAuthenticated: boolean
  name: string | null
  email: string | null
  roles: string[]
  departmentCodes: string[]
  sessionExpiresAt: IsoDateTime | null
}

// ---------- Organisation ----------
export interface Department {
  id: string
  name: string
  costCenterCode: string
  isActive: boolean
  teamCount: number
  createdAt: IsoDateTime
}
export interface CreateDepartmentRequest {
  name: string
  costCenterCode: string
}
export interface UpdateDepartmentRequest {
  name: string
  costCenterCode: string
  isActive: boolean
}

export interface Team {
  id: string
  departmentId: string
  departmentName: string
  name: string
  description: string | null
  isActive: boolean
  keyCount: number
  createdAt: IsoDateTime
}
export interface CreateTeamRequest {
  departmentId: string
  name: string
  description?: string | null
}
export interface UpdateTeamRequest {
  name: string
  description?: string | null
  isActive: boolean
}

// ---------- Virtual keys ----------
export interface VirtualKey {
  id: string
  teamId: string
  teamName: string
  departmentId: string
  departmentName: string
  name: string
  description: string | null
  prefix: string
  status: KeyStatus
  isEnabled: boolean
  createdAt: IsoDateTime
  createdBy: string | null
  expiresAt: IsoDateTime | null
  revokedAt: IsoDateTime | null
  graceUntil: IsoDateTime | null
  lastUsedAt: IsoDateTime | null
  /** Empty = all models allowed. */
  allowedModels: string[]
  /** Empty = all residencies allowed. */
  allowedResidencies: DataResidency[]
  piiPolicy: PiiPolicy
  requestsPerMinute: number | null
  tokensPerMinute: number | null
  rotatedToKeyId: string | null
}

export interface KeyFilter {
  teamId?: string
  departmentId?: string
  status?: KeyStatus
}

export interface CreateKeyRequest {
  teamId: string
  name: string
  description?: string | null
  expiresAt?: IsoDateTime | null
  allowedModels: string[]
  allowedResidencies: DataResidency[]
  piiPolicy: PiiPolicy
  requestsPerMinute?: number | null
  tokensPerMinute?: number | null
}

export interface CreateKeyResponse {
  key: VirtualKey
  /** Shown once, never retrievable again. */
  secret: string
}

export interface UpdateKeyRequest {
  name: string
  description?: string | null
  expiresAt?: IsoDateTime | null
  allowedModels: string[]
  allowedResidencies: DataResidency[]
  piiPolicy: PiiPolicy
  requestsPerMinute?: number | null
  tokensPerMinute?: number | null
  isEnabled: boolean
}

export interface RotateKeyRequest {
  mode: KeyRotationMode
}

export interface RotateKeyResponse {
  key: VirtualKey
  secret: string
  previousKey: VirtualKey
}

// ---------- Providers, models, routes ----------
export interface Provider {
  id: string
  name: string
  displayName: string | null
  type: ProviderType
  baseUrl: string
  authMode: ProviderAuthMode
  hasCredential: boolean
  residency: DataResidency
  capabilities: ProviderCapability[]
  isEnabled: boolean
  isDrained: boolean
  timeoutSeconds: number
  createdAt: IsoDateTime
  deploymentCount: number
}

export interface ProviderRequest {
  name: string
  displayName?: string | null
  type: ProviderType
  baseUrl: string
  authMode: ProviderAuthMode
  /** Write-only. On update: omitted/null = unchanged, "" = remove. */
  credential?: string | null
  residency: DataResidency
  capabilities: ProviderCapability[]
  timeoutSeconds: number
  isEnabled: boolean
}

export interface DrainRequest {
  drained: boolean
}

export interface Price {
  inputPerMillionUsd: number
  cachedInputPerMillionUsd: number
  outputPerMillionUsd: number
  effectiveFrom: IsoDateTime
}

export interface NewPrice {
  inputPerMillionUsd: number
  cachedInputPerMillionUsd: number
  outputPerMillionUsd: number
  /** Optional, defaults to now. */
  effectiveFrom?: IsoDateTime | null
}

export interface Model {
  id: string
  providerId: string
  providerName: string
  residency: DataResidency
  name: string
  upstreamModel: string
  kind: ModelKind
  parameterProfile: ParameterProfile
  contextWindow: number | null
  isEnabled: boolean
  features: string[]
  currentPrice: Price | null
}

export interface CreateModelRequest {
  providerId: string
  name: string
  upstreamModel: string
  kind: ModelKind
  parameterProfile: ParameterProfile
  contextWindow?: number | null
  isEnabled: boolean
  features?: string[]
  price?: NewPrice
}

export interface UpdateModelRequest {
  name: string
  upstreamModel: string
  kind: ModelKind
  parameterProfile: ParameterProfile
  contextWindow?: number | null
  isEnabled: boolean
  features?: string[]
}

export interface RouteTarget {
  modelId: string
  modelName: string
  providerName: string
  residency: DataResidency
  priority: number
  weight: number
}

export interface Route {
  id: string
  name: string
  description: string | null
  kind: ModelKind
  isEnabled: boolean
  targets: RouteTarget[]
}

export interface RouteTargetRequest {
  modelId: string
  priority: number
  weight: number
}

export interface RouteRequest {
  name: string
  description?: string | null
  kind: ModelKind
  isEnabled: boolean
  targets: RouteTargetRequest[]
}

// ---------- Budgets & alerts ----------
export interface Budget {
  id: string
  scope: BudgetScope
  scopeId: string
  scopeName: string
  limitSek: number
  period: BudgetPeriod
  alertThresholds: number[]
  isActive: boolean
  periodStart: IsoDateTime
  periodEnd: IsoDateTime
  spentSek: number
  percentUsed: number
}

export interface BudgetRequest {
  scope: BudgetScope
  scopeId: string
  limitSek: number
  period: BudgetPeriod
  alertThresholds: number[]
  isActive: boolean
}

export interface BudgetFilter {
  scope?: BudgetScope
  scopeId?: string
}

export interface BudgetAlert {
  id: string
  budgetId: string
  scope: BudgetScope
  scopeName: string
  thresholdPercent: number
  spentSek: number
  limitSek: number
  periodStart: IsoDateTime
  timestamp: IsoDateTime
  acknowledged: boolean
}

export interface ExchangeRate {
  currency: 'USD'
  sekPerUnit: number
  effectiveFrom: IsoDateTime
}

export interface UpdateExchangeRateRequest {
  sekPerUnit: number
}

// ---------- Usage ----------
export const USAGE_GROUP_BY = ['department', 'team', 'key', 'model', 'provider', 'day'] as const
export type UsageGroupBy = (typeof USAGE_GROUP_BY)[number]

export interface UsageSummaryQuery {
  from?: string
  to?: string
  groupBy?: UsageGroupBy
  departmentId?: string
  teamId?: string
}

export interface UsageSummaryRow {
  key: string
  label: string
  requests: number
  inputTokens: number
  outputTokens: number
  costSek: number
  errors: number
  fallbacks: number
}

export interface UsageSummary {
  from: IsoDateTime
  to: IsoDateTime
  totalCostSek: number
  totalRequests: number
  totalInputTokens: number
  totalOutputTokens: number
  totalErrors: number
  rows: UsageSummaryRow[]
}

export interface UsageRequestsQuery {
  from?: string
  to?: string
  keyId?: string
  teamId?: string
  departmentId?: string
  outcome?: RequestOutcome
  page?: number
  pageSize?: number
}

export interface UsageRequest {
  requestId: string
  timestamp: IsoDateTime
  keyPrefix: string
  keyName: string
  teamName: string
  departmentName: string
  endpoint: string
  requestedModel: string
  providerName: string | null
  upstreamModel: string | null
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  costSek: number
  latencyMs: number
  statusCode: number
  outcome: RequestOutcome
  fallbackCount: number
  streamed: boolean
  piiActionApplied: string | null
  piiCategories: string[] | string | null
  errorCode: string | null
}

export interface Paged<T> {
  items: T[]
  total: number
}

// ---------- Catalogue ----------
export interface CatalogRoute {
  name: string
  description: string | null
  kind: ModelKind
  residencies: DataResidency[]
  capabilities: ProviderCapability[]
  inputSekPerMillion: number | null
  outputSekPerMillion: number | null
}

export interface CatalogModel {
  name: string
  kind: ModelKind
  residency: DataResidency
  capabilities: ProviderCapability[]
  inputSekPerMillion: number | null
  outputSekPerMillion: number | null
}

export interface Catalog {
  gatewayBaseUrl: string
  routes: CatalogRoute[]
  models: CatalogModel[]
}

// ---------- Operations ----------
export interface HealthComponent {
  name: string
  status: HealthStatus
  description: string | null
}

export interface OpsProviderHealth {
  id: string
  name: string
  type: ProviderType
  residency: DataResidency
  isEnabled: boolean
  isDrained: boolean
  circuitState: CircuitState
  requests24h: number
  errorRate24h: number
  fallbackRate24h: number
  p50LatencyMs: number | null
  p95LatencyMs: number | null
}

export interface OpsHealth {
  checkedAt: IsoDateTime
  components: HealthComponent[]
  versions: { adminApi: string; gateway: string | null; schema: string }
  usageWriter: { queueDepth: number; capacity: number; inFlightRecords: number; lastWriteAt: IsoDateTime | null; consecutiveFailures: number } | null
  providers: OpsProviderHealth[]
}

export interface CircuitRequest {
  state: CircuitState
}

/** Config export document – treated as opaque JSON by the UI. */
export type ConfigDocument = Record<string, unknown>

export interface ConfigImportResult {
  created: number
  updated: number
  skipped: number
}

export interface AuditEntry {
  id: string
  timestamp: IsoDateTime
  actor: string
  action: string
  entityType: string
  entityId: string | null
  details: string | Record<string, unknown> | null
}

export interface AuditQuery {
  page?: number
  pageSize?: number
  entityType?: string
}

// ---------- Gateway (data plane) error codes, for the developer portal ----------
export const GATEWAY_ERROR_CODES = [
  'invalid_api_key',
  'key_expired',
  'key_revoked',
  'key_disabled',
  'model_not_allowed',
  'model_not_found',
  'budget_exceeded',
  'rate_limited',
  'pii_blocked',
  'no_eligible_provider',
  'all_providers_failed',
  'invalid_request',
] as const
export type GatewayErrorCode = (typeof GATEWAY_ERROR_CODES)[number]

export interface DiscoveredModel {
  id: string
  displayName: string
  kind: ModelKind
  parameterProfile: ParameterProfile
  contextWindow: number | null
  features: string[]
  price: { inputPerMillionUsd: number; cachedInputPerMillionUsd: number; outputPerMillionUsd: number } | null
  alreadyAdded: boolean
}
