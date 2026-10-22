import { describe, expect, it } from 'vitest'
import type { Budget, BudgetAlert } from '@/api/types'
import { alertSpentOf, budgetKind, budgetSpentOf, isTokenQuota } from './budgets'

const base = { id: 'b', scope: 'Team', scopeId: 't', scopeName: 'Team', period: 'Monthly', alertThresholds: [80], isActive: true, periodStart: '', periodEnd: '', percentUsed: 0 } as const

describe('budget amounts', () => {
  it('shows a cost budget in USD and treats an older server (no unit) as one', () => {
    const cost: Budget = { ...base, alertThresholds: [80], limitUsd: 100, spentUsd: 12.5 }
    expect(isTokenQuota(cost)).toBe(false)
    expect(budgetSpentOf(cost).limit).toContain('100')
    expect(budgetKind(cost)).toBe('budget')
  })

  it('shows a token quota in tokens, with the residency it counts', () => {
    const quota: Budget = { ...base, alertThresholds: [80], limitUsd: 0, spentUsd: 0, unit: 'Tokens', limitTokens: 5_000_000, spentTokens: 1_200, residency: 'OnPrem' }
    expect(isTokenQuota(quota)).toBe(true)
    expect(budgetSpentOf(quota).limit).toMatch(/tokens$/)
    expect(budgetKind(quota)).toBe('token quota (On-prem)')
  })

  it('words alerts in their unit', () => {
    const alert = { id: 'a', budgetId: 'b', scope: 'Team', scopeName: 'T', thresholdPercent: 80, spentUsd: 0, limitUsd: 0, periodStart: '', timestamp: '', acknowledged: false, spentTokens: 4_000, limitTokens: 5_000 } as BudgetAlert
    expect(alertSpentOf(alert)).toMatch(/of .*tokens$/)
    expect(alertSpentOf({ ...alert, spentTokens: null, limitTokens: null, spentUsd: 4, limitUsd: 5 })).not.toContain('tokens')
  })
})
