import { BUDGET_PERIODS, BUDGET_SCOPES, DATA_RESIDENCIES, MODEL_KINDS, PARAMETER_PROFILES, PROVIDER_AUTH_MODES, PROVIDER_CAPABILITIES, PROVIDER_TYPES } from '@/api/types'

export type Resource = 'departments' | 'teams' | 'providers' | 'models' | 'routes' | 'budgets' | 'prices'
export type FieldType = 'text' | 'number' | 'checkbox' | 'select' | 'multi' | 'password' | 'datetime-local' | 'targets'
export interface Field {
  name: string
  type: FieldType
  required?: boolean
  options?: readonly string[]
  source?: 'departments' | 'teams' | 'providers' | 'models' | 'scopes'
  min?: number
  max?: number
  default?: string | number | boolean | string[]
  immutable?: boolean
}
const name: Field = { name: 'name', type: 'text', required: true }
const description: Field = { name: 'description', type: 'text' }
const enabled: Field = { name: 'isEnabled', type: 'checkbox', default: true }
const kind: Field = { name: 'kind', type: 'select', options: MODEL_KINDS, default: 'Chat' }
export const priceFields: Field[] = [
  { name: 'inputPerMillionUsd', type: 'number', min: 0, required: true, default: 0 },
  { name: 'cachedInputPerMillionUsd', type: 'number', min: 0, required: true, default: 0 },
  { name: 'outputPerMillionUsd', type: 'number', min: 0, required: true, default: 0 },
  { name: 'effectiveFrom', type: 'datetime-local' },
]
export const resourceFields: Record<Resource, Field[]> = {
  departments: [name, { name: 'costCenterCode', type: 'text', required: true }, { name: 'isActive', type: 'checkbox', default: true }],
  teams: [{ name: 'departmentId', type: 'select', source: 'departments', required: true, immutable: true }, name, description, { name: 'isActive', type: 'checkbox', default: true }],
  providers: [name, { name: 'displayName', type: 'text' }, { name: 'type', type: 'select', options: PROVIDER_TYPES, default: 'OpenAICompatible' },
    { name: 'baseUrl', type: 'text', required: true }, { name: 'authMode', type: 'select', options: PROVIDER_AUTH_MODES, default: 'None' },
    { name: 'credential', type: 'password' }, { name: 'residency', type: 'select', options: DATA_RESIDENCIES, default: 'OnPrem' },
    { name: 'capabilities', type: 'multi', options: PROVIDER_CAPABILITIES, default: ['ChatCompletions', 'Streaming'] },
    { name: 'timeoutSeconds', type: 'number', required: true, min: 1, max: 900, default: 120 }, enabled],
  models: [{ name: 'providerId', type: 'select', source: 'providers', required: true, immutable: true }, name,
    { name: 'upstreamModel', type: 'text', required: true }, kind, { name: 'parameterProfile', type: 'select', options: PARAMETER_PROFILES, default: 'Standard' },
    { name: 'contextWindow', type: 'number', min: 1 }, enabled],
  routes: [name, description, kind, { name: 'targets', type: 'targets', source: 'models', required: true }, enabled],
  budgets: [{ name: 'scope', type: 'select', options: BUDGET_SCOPES, default: 'Team' },
    { name: 'scopeId', type: 'select', source: 'scopes', required: true },
    { name: 'limitSek', type: 'number', required: true, min: 0, default: 100 },
    { name: 'period', type: 'select', options: BUDGET_PERIODS, default: 'Monthly' },
    { name: 'alertThresholds', type: 'text', required: true, default: '50,80,100' },
    { name: 'isActive', type: 'checkbox', default: true }],
  prices: priceFields,
}
export const resourceColumns: Record<Resource, string[]> = {
  departments: ['name', 'costCenterCode', 'isActive'],
  teams: ['name', 'departmentName', 'isActive'],
  providers: ['name', 'residency', 'hasCredential', 'isEnabled'],
  models: ['name', 'providerName', 'kind', 'isEnabled'],
  routes: ['name', 'kind', 'targets', 'isEnabled'],
  budgets: ['scopeName', 'limitSek', 'spentSek', 'period'],
  prices: ['effectiveFrom', 'inputPerMillionUsd', 'cachedInputPerMillionUsd', 'outputPerMillionUsd'],
}
