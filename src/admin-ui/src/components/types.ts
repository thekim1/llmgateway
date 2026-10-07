import type { DataResidency, PiiPolicy } from '@/api/types'

export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral'

export interface ModelOption {
  name: string
  /** "route" = model alias with fallback; "model" = a single deployment. */
  source: 'route' | 'model'
  residencies: DataResidency[]
}

/** Values edited by KeyForm (create, edit and the team wizard). */
export interface KeyFormValues {
  teamId: string
  name: string
  description: string
  /** YYYY-MM-DD or empty. */
  expiresAt: string
  allowedModels: string[]
  allowedResidencies: DataResidency[]
  piiPolicy: PiiPolicy
  requestsPerMinute: number | null
  tokensPerMinute: number | null
  isEnabled: boolean
}

export interface BarRow {
  key: string
  label: string
  value: number
  valueText: string
  /** Extra cells shown after the label (already formatted). */
  extra?: string[]
}

