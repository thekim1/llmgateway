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
  ConfigDocument,
  ConfigImportResult,
  CreateDepartmentRequest,
  CreateKeyRequest,
  CreateKeyResponse,
  CreateModelRequest,
  CreateTeamRequest,
  Department,
  ExchangeRate,
  KeyFilter,
  KeyRotationMode,
  Model,
  NewPrice,
  OpsHealth,
  Paged,
  Price,
  Provider,
  ProviderRequest,
  RotateKeyResponse,
  Route,
  RouteRequest,
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
    remove: (deptId: string) => http.del(`/api/departments/${id(deptId)}`),
  },
  teams: {
    list: (departmentId?: string) => http.get<Team[]>('/api/teams', { departmentId }),
    create: (body: CreateTeamRequest) => http.post<Team>('/api/teams', body),
    update: (teamId: string, body: UpdateTeamRequest) => http.put<Team>(`/api/teams/${id(teamId)}`, body),
    remove: (teamId: string) => http.del(`/api/teams/${id(teamId)}`),
  },
  keys: {
    list: (filter: KeyFilter = {}) => http.get<VirtualKey[]>('/api/keys', { ...filter }),
    get: (keyId: string) => http.get<VirtualKey>(`/api/keys/${id(keyId)}`),
    create: (body: CreateKeyRequest) => http.post<CreateKeyResponse>('/api/keys', body),
    update: (keyId: string, body: UpdateKeyRequest) => http.put<VirtualKey>(`/api/keys/${id(keyId)}`, body),
    rotate: (keyId: string, mode: KeyRotationMode) =>
      http.post<RotateKeyResponse>(`/api/keys/${id(keyId)}/rotate`, { mode }),
    revoke: (keyId: string) => http.post<VirtualKey>(`/api/keys/${id(keyId)}/revoke`),
  },
  providers: {
    list: () => http.get<Provider[]>('/api/providers'),
    create: (body: ProviderRequest) => http.post<Provider>('/api/providers', body),
    update: (providerId: string, body: ProviderRequest) =>
      http.put<Provider>(`/api/providers/${id(providerId)}`, body),
    drain: (providerId: string, drained: boolean) =>
      http.post<Provider>(`/api/providers/${id(providerId)}/drain`, { drained }),
    remove: (providerId: string) => http.del(`/api/providers/${id(providerId)}`),
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
  },
  catalog: {
    get: () => http.get<Catalog>('/api/catalog'),
  },
  ops: {
    health: () => http.get<OpsHealth>('/api/ops/health'),
    setCircuit: (providerId: string, state: CircuitState) =>
      http.post<void>(`/api/ops/providers/${id(providerId)}/circuit`, { state }),
    invalidateKeyCache: () => http.post<void>('/api/ops/cache/invalidate-keys'),
    exportConfig: () => http.get<ConfigDocument>('/api/ops/config/export'),
    importConfig: (doc: ConfigDocument) => http.post<ConfigImportResult>('/api/ops/config/import', doc),
  },
  audit: {
    list: (query: AuditQuery) => http.get<Paged<AuditEntry>>('/api/audit', { ...query }),
  },
}

export type Api = typeof api
