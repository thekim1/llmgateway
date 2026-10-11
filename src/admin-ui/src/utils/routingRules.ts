import type { RoutingRule, RoutingRuleRequest, RoutingScope } from '@/api/types'
import { ROUTING_SCOPES } from '@/api/types'

/** The rules of one scope, in checking order. A request is only checked against the rules that apply to its own key, team and department. */
export interface ScopeSection {
  scope: RoutingScope
  rules: RoutingRule[]
}

const byOrder = (a: RoutingRule, b: RoutingRule) => a.priority - b.priority || a.name.localeCompare(b.name, 'sv')

/**
 * Groups rules the way the gateway checks them: scope by scope (key, team, department, global), each scope by priority then name.
 * A rule can be shared by several keys, teams or departments, so the order is kept per scope, not per owner.
 */
export function groupRules(rules: readonly RoutingRule[]): ScopeSection[] {
  const sections: ScopeSection[] = []
  for (const scope of ROUTING_SCOPES) {
    const ofScope = rules.filter((r) => r.scope === scope)
    if (ofScope.length > 0) sections.push({ scope, rules: [...ofScope].sort(byOrder) })
  }
  return sections
}

/** Who a rule applies to, in a few words: "Everyone", "Kundtjänst", "Kundtjänst, Ekonomi +2", "No owner". */
export function ownerSummary(rule: RoutingRule, shown = 2): string {
  if (rule.scope === 'Global') return 'Every request'
  const names = rule.owners.map((o) => o.name).filter((n): n is string => !!n)
  if (names.length === 0) return 'No owner'
  const more = names.length - shown
  return names.slice(0, shown).join(', ') + (more > 0 ? ` +${more}` : '')
}

/** The request that re-saves a rule unchanged (used to flip a single field, such as enabled). */
export function toRequest(rule: RoutingRule, patch: Partial<RoutingRuleRequest> = {}): RoutingRuleRequest {
  return {
    name: rule.name,
    description: rule.description,
    scope: rule.scope,
    scopeIds: [...rule.scopeIds],
    isEnabled: rule.isEnabled,
    priority: rule.priority,
    condition: rule.condition,
    chain: rule.chain,
    targets: rule.targets.map((t) => ({ ...t })),
    fallbacks: [...rule.fallbacks],
    ...patch,
  }
}

/** Each target's share of the traffic, as a whole percentage that always adds up to 100. */
export function targetShares(weights: readonly number[]): number[] {
  const total = weights.reduce((sum, w) => sum + Math.max(0, w), 0)
  if (total <= 0) return weights.map(() => 0)
  const raw = weights.map((w) => (Math.max(0, w) / total) * 100)
  const shares = raw.map(Math.floor)
  let remainder = 100 - shares.reduce((a, b) => a + b, 0)
  const order = raw.map((r, i) => ({ i, frac: r - Math.floor(r) })).sort((a, b) => b.frac - a.frac)
  for (const { i } of order) {
    if (remainder <= 0) break
    shares[i] = (shares[i] ?? 0) + 1
    remainder--
  }
  return shares
}

/** A rule is in effect when it is enabled, has an owner and the gateway accepts it. */
export function isInEffect(rule: RoutingRule): boolean {
  return rule.isEnabled && !rule.isOrphaned && rule.validationErrors.length === 0
}
