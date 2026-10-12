import { buildUrl, http, postLogout } from './client'
import type {
  AuditEntry,
  AuditQuery,
  BffUser,
  Budget,
  BudgetAlert,
  BudgetFilter,
  BudgetRequest,
  Catalog,
  CircuitState,
  ConditionCheck,
  ConfigDocument,
  ConfigImportResult,
  CreateDataApiKeyResponse,
  CreateDepartmentRequest,
  CreateKeyRequest,
  CreateKeyResponse,
  CreateModelRequest,
  CreateTeamRequest,
  DataAccessSettings,
  DataApiKey,
  DataApiKeyRequest,
  Department,
  DiscoveredModel,
  ExchangeRate,
  KeyFilter,
  KeyRotationMode,
  McpGrant,
  McpGrantRequest,
  McpServer,
  McpServerRequest,
  McpSyncResult,
  McpTestResult,
  McpTool,
  Model,
  NewPrice,
  OpsHealth,
  Paged,
  PiiIncidentReport,
  PiiIncidentsQuery,
  Price,
  Provider,
  ProviderRequest,
  ProviderStability,
  ProviderStatsQuery,
  RotateKeyResponse,
  Route,
  RouteRequest,
  RoutingRule,
  RoutingRuleFilter,
  RoutingRuleReassignRequest,
  RoutingRuleReorderRequest,
  RoutingRuleRequest,
  RoutingRuleTestRequest,
  RoutingRuleTestResult,
  RoutingRulesChoice,
  Team,
  UpdateDepartmentRequest,
  UpdateKeyRequest,
  UpdateModelRequest,
  UpdateTeamRequest,
  UsageRequest,
  UsageRequestsQuery,
  UsageSummary,
  UsageSummaryQuery,
  VirtualKey,
} from './types'

const id = (value: string) => encodeURIComponent(value)

export const api = {
  bff: {
    user: () => http.get<BffUser>('/bff/user'),
    logout: () => postLogout(),
    extendSession: () => http.post<{ sessionExpiresAt: string }>('/bff/session/extend'),
    loginUrl: (returnUrl: string) => buildUrl('/bff/login', { returnUrl }),
  },
  departments: {
    list: () => http.get<Department[]>('/api/departments'),
    create: (body: CreateDepartmentRequest) => http.post<Department>('/api/departments', body),
    update: (deptId: string, body: UpdateDepartmentRequest) =>
      http.put<Department>(`/api/departments/${id(deptId)}`, body),
    /** `routingRules` is required when the department has routing rules (the API answers 409 with the list otherwise). */
    remove: (deptId: string, routingRules?: RoutingRulesChoice) =>
      http.del(buildUrl(`/api/departments/${id(deptId)}`, { routingRules })),
  },
  teams: {
    list: (departmentId?: string) => http.get<Team[]>('/api/teams', { departmentId }),
    create: (body: CreateTeamRequest) => http.post<Team>('/api/teams', body),
    update: (teamId: string, body: UpdateTeamRequest) => http.put<Team>(`/api/teams/${id(teamId)}`, body),
    /** `routingRules` is required when the team has routing rules (the API answers 409 with the list otherwise). */
    remove: (teamId: string, routingRules?: RoutingRulesChoice) => http.del(buildUrl(`/api/teams/${id(teamId)}`, { routingRules })),
  },
  keys: {
    list: (filter: KeyFilter = {}) => http.get<VirtualKey[]>('/api/keys', { ...filter }),
    get: (keyId: string) => http.get<VirtualKey>(`/api/keys/${id(keyId)}`),
    create: (body: CreateKeyRequest) => http.post<CreateKeyResponse>('/api/keys', body),
    update: (keyId: string, body: UpdateKeyRequest) => http.put<VirtualKey>(`/api/keys/${id(keyId)}`, body),
    rotate: (keyId: string, mode: KeyRotationMode) =>
      http.post<RotateKeyResponse>(`/api/keys/${id(keyId)}/rotate`, { mode }),
    revoke: (keyId: string) => http.post<VirtualKey>(`/api/keys/${id(keyId)}/revoke`),
    /** Grants on the MCP servers the user can see. */
    mcpGrants: (keyId: string) => http.get<McpGrant[]>(`/api/keys/${id(keyId)}/mcp-grants`),
    /** Replaces the key's grants on the servers the user can see. */
    replaceMcpGrants: (keyId: string, grants: McpGrantRequest[]) => http.put<McpGrant[]>(`/api/keys/${id(keyId)}/mcp-grants`, grants),
    /** gateway-admin only. Every call is written to the audit log. */
    reveal: (keyId: string, purpose: 'Reveal' | 'Copy') =>
      http.post<{ secret: string }>(`/api/keys/${id(keyId)}/reveal`, { purpose }),
  },
  /** gateway-admin only. Data API keys; the secret is only in the create response. */
  dataAccess: {
    settings: () => http.get<DataAccessSettings>('/api/data-access'),
    list: () => http.get<DataApiKey[]>('/api/data-access/keys'),
    create: (body: DataApiKeyRequest) => http.post<CreateDataApiKeyResponse>('/api/data-access/keys', body),
    update: (keyId: string, body: DataApiKeyRequest) => http.put<DataApiKey>(`/api/data-access/keys/${id(keyId)}`, body),
    revoke: (keyId: string) => http.post<DataApiKey>(`/api/data-access/keys/${id(keyId)}/revoke`),
    /** Only revoked or expired keys can be deleted. */
    remove: (keyId: string) => http.del(`/api/data-access/keys/${id(keyId)}`),
  },
  providers: {
    list: () => http.get<Provider[]>('/api/providers'),
    create: (body: ProviderRequest) => http.post<Provider>('/api/providers', body),
    update: (providerId: string, body: ProviderRequest) =>
      http.put<Provider>(`/api/providers/${id(providerId)}`, body),
    drain: (providerId: string, drained: boolean) =>
      http.post<Provider>(`/api/providers/${id(providerId)}/drain`, { drained }),
    remove: (providerId: string) => http.del(`/api/providers/${id(providerId)}`),
    discoverModels: (providerId: string) => http.post<DiscoveredModel[]>(`/api/providers/${id(providerId)}/discover-models`, {}),
  },
  mcp: {
    list: () => http.get<McpServer[]>('/api/mcp/servers'),
    create: (body: McpServerRequest) => http.post<McpServer>('/api/mcp/servers', body),
    update: (serverId: string, body: McpServerRequest) => http.put<McpServer>(`/api/mcp/servers/${id(serverId)}`, body),
    remove: (serverId: string) => http.del(`/api/mcp/servers/${id(serverId)}`),
    /** Connects and lists the tools without saving: a new server (no id), or a saved one with optional unsaved changes. */
    test: (serverId: string | null, body?: McpServerRequest) =>
      serverId ? http.post<McpTestResult>(`/api/mcp/servers/${id(serverId)}/test`, body) : http.post<McpTestResult>('/api/mcp/servers/test', body),
    /** 409 while another sync runs. */
    sync: (serverId: string) => http.post<McpSyncResult>(`/api/mcp/servers/${id(serverId)}/sync`),
    tools: (serverId: string) => http.get<McpTool[]>(`/api/mcp/servers/${id(serverId)}/tools`),
    /** 409 when the definition changed since `definitionHash` was reviewed. */
    approve: (serverId: string, toolId: string, definitionHash: string) =>
      http.post<McpTool>(`/api/mcp/servers/${id(serverId)}/tools/${id(toolId)}/approve`, { definitionHash }),
    reject: (serverId: string, toolId: string) => http.post<McpTool>(`/api/mcp/servers/${id(serverId)}/tools/${id(toolId)}/reject`),
    setPrice: (serverId: string, toolId: string, priceSek: number | null) =>
      http.put<McpTool>(`/api/mcp/servers/${id(serverId)}/tools/${id(toolId)}/price`, { priceSek }),
  },
  models: {
    list: () => http.get<Model[]>('/api/models'),
    create: (body: CreateModelRequest) => http.post<Model>('/api/models', body),
    update: (modelId: string, body: UpdateModelRequest) => http.put<Model>(`/api/models/${id(modelId)}`, body),
    remove: (modelId: string) => http.del(`/api/models/${id(modelId)}`),
    prices: (modelId: string) => http.get<Price[]>(`/api/models/${id(modelId)}/prices`),
    addPrice: (modelId: string, body: NewPrice) => http.post<Price>(`/api/models/${id(modelId)}/prices`, body),
  },
  routes: {
    list: () => http.get<Route[]>('/api/routes'),
    create: (body: RouteRequest) => http.post<Route>('/api/routes', body),
    update: (routeId: string, body: RouteRequest) => http.put<Route>(`/api/routes/${id(routeId)}`, body),
    remove: (routeId: string) => http.del(`/api/routes/${id(routeId)}`),
  },
  routingRules: {
    list: (filter: RoutingRuleFilter = {}) => http.get<RoutingRule[]>('/api/routing-rules', { ...filter }),
    get: (ruleId: string) => http.get<RoutingRule>(`/api/routing-rules/${id(ruleId)}`),
    create: (body: RoutingRuleRequest) => http.post<RoutingRule>('/api/routing-rules', body),
    update: (ruleId: string, body: RoutingRuleRequest) => http.put<RoutingRule>(`/api/routing-rules/${id(ruleId)}`, body),
    remove: (ruleId: string) => http.del(`/api/routing-rules/${id(ruleId)}`),
    /** The on/off switch. Turning a rule on fails (400, with the reason) while its owners or models do not exist. */
    setEnabled: (ruleId: string, isEnabled: boolean) => http.put<RoutingRule>(`/api/routing-rules/${id(ruleId)}/enabled`, { isEnabled }),
    /** Attach a rule (for example one deactivated when its last team was deleted) to new owners. */
    reassign: (ruleId: string, body: RoutingRuleReassignRequest) => http.post<RoutingRule>(`/api/routing-rules/${id(ruleId)}/reassign`, body),
    /** Sets the checking order within one scope (all key rules, all team rules, ...); returns the reordered rules. */
    reorder: (body: RoutingRuleReorderRequest) => http.post<RoutingRule[]>('/api/routing-rules/reorder', body),
    /** Live validation of a condition while it is typed. */
    validate: (condition: string) => http.post<ConditionCheck>('/api/routing-rules/validate', { condition }),
    /** Dry run against the stored, enabled rules. */
    test: (body: RoutingRuleTestRequest) => http.post<RoutingRuleTestResult>('/api/routing-rules/test', body),
  },
  budgets: {
    list: (filter: BudgetFilter = {}) => http.get<Budget[]>('/api/budgets', { ...filter }),
    create: (body: BudgetRequest) => http.post<Budget>('/api/budgets', body),
    update: (budgetId: string, body: BudgetRequest) => http.put<Budget>(`/api/budgets/${id(budgetId)}`, body),
    remove: (budgetId: string) => http.del(`/api/budgets/${id(budgetId)}`),
  },
  alerts: {
    list: (acknowledged?: boolean) => http.get<BudgetAlert[]>('/api/alerts', { acknowledged }),
    acknowledge: (alertId: string) => http.post<void>(`/api/alerts/${id(alertId)}/acknowledge`),
  },
  settings: {
    exchangeRate: () => http.get<ExchangeRate>('/api/settings/exchange-rate'),
    updateExchangeRate: (sekPerUnit: number) =>
      http.put<ExchangeRate>('/api/settings/exchange-rate', { sekPerUnit }),
  },
  usage: {
    summary: (query: UsageSummaryQuery) => http.get<UsageSummary>('/api/usage/summary', { ...query }),
    requests: (query: UsageRequestsQuery) => http.get<Paged<UsageRequest>>('/api/usage/requests', { ...query }),
    request: (requestId: string) => http.get<UsageRequest>(`/api/usage/requests/${id(requestId)}`),
    exportCsvUrl: (from?: string, to?: string) => buildUrl('/api/usage/export.csv', { from, to }),
    piiIncidents: (query: PiiIncidentsQuery) => http.get<PiiIncidentReport>('/api/usage/pii-incidents', { ...query }),
  },
  catalog: {
    get: () => http.get<Catalog>('/api/catalog'),
  },
  ops: {
    health: () => http.get<OpsHealth>('/api/ops/health'),
    providerStats: (query: ProviderStatsQuery) => http.get<ProviderStability>('/api/ops/providers/stats', { ...query }),
    setCircuit: (providerId: string, state: CircuitState) =>
      http.post<void>(`/api/ops/providers/${id(providerId)}/circuit`, { state }),
    setMcpCircuit: (serverId: string, state: CircuitState) =>
      http.post<void>(`/api/ops/mcp-servers/${id(serverId)}/circuit`, { state }),
    invalidateKeyCache: () => http.post<void>('/api/ops/cache/invalidate-keys'),
    exportConfig: () => http.get<ConfigDocument>('/api/ops/config/export'),
    importConfig: (doc: ConfigDocument) => http.post<ConfigImportResult>('/api/ops/config/import', doc),
  },
  audit: {
    list: (query: AuditQuery) => http.get<Paged<AuditEntry>>('/api/audit', { ...query }),
  },
}

export type Api = typeof api
