import type { Budget, BudgetAlert, DataResidency } from '@/api/types'
import { formatNumber, formatUsdCost } from './format'
import { RESIDENCY } from './labels'

/** A token quota (counts input plus output tokens) rather than a cost budget in USD. */
export const isTokenQuota = (budget: Pick<Budget, 'unit'>): boolean => budget.unit === 'Tokens'

const tokens = (value: number | null | undefined): string => `${formatNumber(value)} tokens`

/** "12 of 100" in the budget's unit, for lists. */
export function budgetSpentOf(budget: Budget): { spent: string; limit: string } {
  return isTokenQuota(budget)
    ? { spent: formatNumber(budget.spentTokens), limit: tokens(budget.limitTokens) }
    : { spent: formatUsdCost(budget.spentUsd), limit: formatUsdCost(budget.limitUsd) }
}

/** The alert's amounts in its budget's unit. */
export function alertSpentOf(alert: BudgetAlert): string {
  return alert.spentTokens != null
    ? `${formatNumber(alert.spentTokens)} of ${tokens(alert.limitTokens)}`
    : `${formatUsdCost(alert.spentUsd)} of ${formatUsdCost(alert.limitUsd)}`
}

/** What a budget is called in text: "budget", or "token quota" with the residency it counts. */
export function budgetKind(budget: Pick<Budget, 'unit' | 'residency'>): string {
  if (!isTokenQuota(budget)) return 'budget'
  return budget.residency ? `token quota (${residencyName(budget.residency)})` : 'token quota'
}

const residencyName = (residency: DataResidency): string => RESIDENCY[residency]?.label ?? residency
