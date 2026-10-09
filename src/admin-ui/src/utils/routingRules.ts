import type { RoutingRule, RoutingRuleRequest, RoutingScope } from '@/api/types'
import { ROUTING_SCOPES } from '@/api/types'

/** The rules of one scope that belong to the same owner (a team, a department, a key, or "everyone" for global). Order is the checking order. */
export interface RuleGroup {
  /** Stable key: scope + owner. */
  key: string
  scope: RoutingScope
  scopeId: string | null
  /** Owner name; null for global rules and for owners that no longer exist. */
  ownerName: string | null
  orphaned: boolean
  rules: RoutingRule[]
}

export interface ScopeSection {
  scope: RoutingScope
  groups: RuleGroup[]
}

const byOrder = (a: RoutingRule, b: RoutingRule) => a.priority - b.priority || a.name.localeCompare(b.name, 'sv')

/**
 * Groups rules the way the gateway checks them: scope by scope (key, team, department, global), and within a scope per owner,
 * because only an owner's own rules compete with each other. Rules without an owner come last in their scope.
 */
export function groupRules(rules: readonly RoutingRule[]): ScopeSection[] {
  const sections: ScopeSection[] = []
  for (const scope of ROUTING_SCOPES) {
    const ofScope = rules.filter((r) => r.scope === scope)
    if (ofScope.length === 0) continue
    const groups = new Map<string, RuleGroup>()
    for (const rule of ofScope) {
      const key = `${scope}:${rule.scopeId ?? ''}`
      const group = groups.get(key) ?? {
        key,
        scope,
        scopeId: rule.scopeId,
        ownerName: rule.scopeName,
        orphaned: rule.isOrphaned,
        rules: [],
      }
      group.rules.push(rule)
      groups.set(key, group)
    }
    const ordered = [...groups.values()]
      .map((g) => ({ ...g, rules: [...g.rules].sort(byOrder) }))
      .sort((a, b) => Number(a.orphaned) - Number(b.orphaned) || (a.ownerName ?? '').localeCompare(b.ownerName ?? '', 'sv'))
    sections.push({ scope, groups: ordered })
  }
  return sections
}

/** The request that re-saves a rule unchanged (used to flip a single field, such as enabled). */
export function toRequest(rule: RoutingRule, patch: Partial<RoutingRuleRequest> = {}): RoutingRuleRequest {
  return {
    name: rule.name,
    description: rule.description,
    scope: rule.scope,
    scopeId: rule.scopeId,
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
