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
  'AudioTranscriptions',
  'Realtime',
] as const
export type ProviderCapability = (typeof PROVIDER_CAPABILITIES)[number]

export const MODEL_KINDS = ['Chat', 'Embedding', 'Transcription', 'Realtime', 'SpeechTranslation'] as const
export type ModelKind = (typeof MODEL_KINDS)[number]

export const PARAMETER_PROFILES = ['Standard', 'OpenAIReasoning'] as const
export type ParameterProfile = (typeof PARAMETER_PROFILES)[number]

export const PII_POLICIES = ['Off', 'Allow', 'Redact', 'Block', 'RerouteToOnPrem'] as const
export type PiiPolicy = (typeof PII_POLICIES)[number]

export const ATTACHMENT_POLICIES = ['Allowed', 'ImagesOnly', 'None'] as const
export type AttachmentPolicy = (typeof ATTACHMENT_POLICIES)[number]

export const BUDGET_SCOPES = ['Department', 'Team', 'VirtualKey'] as const
export type BudgetScope = (typeof BUDGET_SCOPES)[number]

export const BUDGET_PERIODS = ['Hourly', 'Daily', 'Weekly', 'Monthly', 'Quarterly', 'Yearly'] as const
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

export const PROVIDER_STATUSES = ['Healthy', 'Degraded', 'Unhealthy', 'CircuitOpen', 'NoRecentTraffic', 'Disabled'] as const
export type ProviderStatus = (typeof PROVIDER_STATUSES)[number]

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
  /** ASP.NET Core environment name of the admin API, for example Development or Production. */
  environment?: string
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
  /** Provider account names the key may use. Empty = all providers. */
  allowedProviders: string[]
  piiPolicy: PiiPolicy
  /** Which file parts (images, documents, audio, video) requests may carry. */
  attachmentPolicy: AttachmentPolicy
  requestsPerMinute: number | null
  tokensPerMinute: number | null
  rotatedToKeyId: string | null
  /** True when the full key is stored encrypted and a gateway-admin can reveal it. */
  canReveal?: boolean
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
  /** Provider account names the key may use. Empty = all; omitted = unchanged. */
  allowedProviders?: string[]
  piiPolicy: PiiPolicy
  /** Omitted = Allowed on create, unchanged on update. */
  attachmentPolicy?: AttachmentPolicy
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
  allowedProviders?: string[]
  piiPolicy: PiiPolicy
  /** Omitted = Allowed on create, unchanged on update. */
  attachmentPolicy?: AttachmentPolicy
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

// ---------- Data API access ----------
/** Data API permissions (consumer classes); the order is the canonical one the API stores them in. */
export const DATA_PERMISSIONS = ['usage.aggregate', 'usage.detail', 'security.read', 'catalog.read'] as const
export type DataPermission = (typeof DATA_PERMISSIONS)[number]

export interface DataAccessSettings {
  /** The Data API's public address; null when the admin API has no DataApi:PublicUrl. */
  dataApiUrl: string | null
  permissions: DataPermission[]
}

/** A key for the read-only Data API. Status is Active, Expired or Revoked. */
export interface DataApiKey {
  id: string
  name: string
  description: string | null
  prefix: string
  permissions: DataPermission[]
  status: KeyStatus
  createdAt: IsoDateTime
  createdBy: string | null
  expiresAt: IsoDateTime | null
  revokedAt: IsoDateTime | null
  /** Approximate: the Data API records use at most every five minutes. */
  lastUsedAt: IsoDateTime | null
}

export interface DataApiKeyRequest {
  name: string
  description?: string | null
  permissions: DataPermission[]
  expiresAt?: IsoDateTime | null
}

export interface CreateDataApiKeyResponse {
  key: DataApiKey
  /** Shown once, never retrievable again. */
  secret: string
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
  /** Speech-to-text models billed by duration (Whisper). */
  audioPerMinuteUsd: number
  /** Audio tokens of realtime and gpt-4o-transcribe models; 0 bills them as text tokens. */
  audioInputPerMillionUsd: number
  audioOutputPerMillionUsd: number
  effectiveFrom: IsoDateTime
}

export interface NewPrice {
  inputPerMillionUsd: number
  cachedInputPerMillionUsd: number
  outputPerMillionUsd: number
  /** Optional, defaults to 0. */
  audioPerMinuteUsd?: number
  /** Optional, defaults to 0 (audio tokens billed as text tokens). */
  audioInputPerMillionUsd?: number
  audioOutputPerMillionUsd?: number
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
  /** What clients call the model: its name, or provider/name when another provider has a model of the same name. */
  publicName: string
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
  /** The routing rule that decided where the request went (the last one when rules were chained); null when none applied. */
  routingRuleId: string | null
  routingRuleName: string | null
}

export interface Paged<T> {
  items: T[]
  total: number
}

// ---------- PII incidents ----------
export const PII_CATEGORIES = ['Personnummer', 'Samordningsnummer', 'Email', 'Phone', 'Iban'] as const
export type PiiCategory = (typeof PII_CATEGORIES)[number]
/** The policies that can be applied to a request with PII (a key with `Off` is never scanned). */
export type PiiAction = Exclude<PiiPolicy, 'Off'>

export interface PiiIncidentsQuery {
  from?: string
  to?: string
  keyId?: string
  teamId?: string
  departmentId?: string
  category?: PiiCategory
  action?: PiiAction
  page?: number
  pageSize?: number
}

export interface PiiFinding {
  category: PiiCategory
  count: number
}

export interface PiiIncident {
  requestId: string
  timestamp: IsoDateTime
  keyId: string
  keyPrefix: string
  keyName: string
  teamId: string
  teamName: string
  departmentId: string
  departmentName: string
  endpoint: string
  requestedModel: string
  providerName: string | null
  action: PiiAction
  categories: PiiFinding[]
  outcome: RequestOutcome
  statusCode: number
  routingRuleName: string | null
}

/** One page of incidents (all filters) plus counts per category and action (period, department, team and key filters only). */
export interface PiiIncidentReport extends Paged<PiiIncident> {
  from: IsoDateTime
  to: IsoDateTime
  totalIncidents: number
  categories: { category: PiiCategory; requests: number; occurrences: number }[]
  actions: { action: PiiAction; requests: number }[]
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
  /** Share of the last 24 h's requests that failed at the provider (5xx, timeouts, broken streams). */
  providerErrorRate24h: number
  /** Computed by the admin API from the last hour; see `statusReason`. */
  status: ProviderStatus
  statusReason: string
  recentRequests: number
  recentProviderErrors: number
}

export type StatsBucket = 'Hour' | 'Day'

export interface ProviderStatsQuery {
  from?: IsoDateTime
  to?: IsoDateTime
  bucket?: 'hour' | 'day'
}

export interface ProviderRank {
  providerId: string
  name: string
  exists: boolean
  requests: number
  providerErrors: number
  /** 1 − provider error rate over the period. */
  availability: number
  fallbackRate: number
  p50LatencyMs: number | null
  p95LatencyMs: number | null
}

export interface ProviderBucket {
  providerId: string
  start: IsoDateTime
  requests: number
  providerErrors: number
  errorRate: number
  fallbacks: number
  p50LatencyMs: number | null
  p95LatencyMs: number | null
}

export interface ProviderStability {
  from: IsoDateTime
  to: IsoDateTime
  bucket: StatsBucket
  timeZone: string
  /** Most stable first. */
  providers: ProviderRank[]
  /** Only buckets with traffic. */
  buckets: ProviderBucket[]
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

// ---------- Routing rules ----------
/** In evaluation order: the most specific scope is checked first. */
export const ROUTING_SCOPES = ['VirtualKey', 'Team', 'Department', 'Global'] as const
export type RoutingScope = (typeof ROUTING_SCOPES)[number]

/** What to do with a team's or department's routing rules when it is deleted. */
export const ROUTING_RULES_CHOICES = ['delete', 'deactivate'] as const
export type RoutingRulesChoice = (typeof ROUTING_RULES_CHOICES)[number]

export interface RuleTarget {
  /** A route alias or model name. */
  model: string
  weight: number
}

/** Why the gateway ignores an enabled rule (position/length are set for condition errors). */
export interface RuleProblem {
  message: string
  position: number | null
  length: number | null
}

/** A key, team or department a rule applies to; `name` is null when it no longer exists. */
export interface RuleOwner {
  id: string
  name: string | null
}

export interface RoutingRule {
  id: string
  /** The rule's title. */
  name: string
  description: string | null
  isEnabled: boolean
  /** A shipped example rule, off until someone adopts it. Editing or enabling it makes it an ordinary rule. */
  isTemplate: boolean
  /** Lower is checked first within a scope. */
  priority: number
  scope: RoutingScope
  /** The keys, teams or departments (of the scope's kind) the rule is shared by; empty for global rules. */
  scopeIds: string[]
  owners: RuleOwner[]
  /** None of the rule's keys/teams/departments exists any more: the rule never applies until it is reassigned. */
  isOrphaned: boolean
  condition: string
  chain: boolean
  targets: RuleTarget[]
  fallbacks: string[]
  /** Targets or fallbacks that do not exist; the rule cannot be enabled until they do. */
  missingModels: string[]
  validationErrors: RuleProblem[]
  createdAt: IsoDateTime
  updatedAt: IsoDateTime
}

export interface RoutingRuleRequest {
  name: string
  description?: string | null
  scope: RoutingScope
  scopeIds: string[]
  isEnabled: boolean
  priority: number
  condition?: string | null
  chain: boolean
  targets: RuleTarget[]
  fallbacks: string[]
}

export interface RoutingRuleFilter {
  scope?: RoutingScope
  scopeId?: string
  orphaned?: boolean
}

export interface RoutingRuleReassignRequest {
  scope: RoutingScope
  scopeIds: string[]
  /** Enable the rule again (default). */
  enable: boolean
}

export interface RoutingRuleReorderRequest {
  scope: RoutingScope
  /** Rule ids in the order they should be checked. */
  ruleIds: string[]
}

export interface ConditionError {
  position: number
  length: number
  message: string
}

export interface ConditionCheck {
  valid: boolean
  errors: ConditionError[]
  /** Variables the condition uses. */
  variables: string[]
  /** Every variable a condition may use. */
  available: { name: string; type: string }[]
}

export interface RoutingRuleTestRequest {
  model: string
  endpoint?: string
  headers?: Record<string, string>
  params?: Record<string, string | number | boolean>
  keyId?: string
  teamId?: string
  departmentId?: string
  budgetUsed?: number
  tokensUsed?: number
  piiDetected?: boolean
  promptTokens?: number
}

export type RuleOutcome = 'Matched' | 'NotMatched' | 'Skipped'

export interface RuleTraceEntry {
  text: string
  leftValue: string | null
  /** null = the comparison could not be evaluated (for example a header that was not sent). */
  result: boolean | null
}

export interface RuleEvaluation {
  ruleId: string
  name: string
  scope: RoutingScope
  priority: number
  chainStep: number
  model: string
  outcome: RuleOutcome
  trace: RuleTraceEntry[]
}

export interface RoutingRuleTestResult {
  matched: boolean
  primaryModel: string | null
  models: { name: string; exists: boolean; type: 'alias' | 'deployment' | 'unknown'; kind: ModelKind | null; enabled: boolean }[]
  applied: { ruleId: string; name: string; fromModel: string; toModel: string }[]
  chainLimitReached: boolean
  evaluation: RuleEvaluation[]
  ignoredRules: { ruleId: string; ruleName: string; message: string; position: number | null; length: number | null }[]
  note: string
}

/** The 409 problem returned when deleting a team/department that still has routing rules and no choice was given. */
export interface ScopedRulesProblem extends ProblemDetails {
  code: 'routing_rules_scoped'
  choices: RoutingRulesChoice[]
  rules: { id: string; name: string; isEnabled: boolean }[]
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
  'attachment_not_allowed',
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
  price: DiscoveredPrice | null
  alreadyAdded: boolean
  /** Where a suggested price came from, e.g. the Azure list price meters of a Foundry deployment. */
  priceSource: string | null
}

export interface DiscoveredPrice {
  inputPerMillionUsd: number
  cachedInputPerMillionUsd: number
  outputPerMillionUsd: number
  audioInputPerMillionUsd?: number
  audioOutputPerMillionUsd?: number
}
