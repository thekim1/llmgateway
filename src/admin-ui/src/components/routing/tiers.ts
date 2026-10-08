import type { DataResidency, OpsProviderHealth, Route, RouteTarget } from '@/api/types'
import { CIRCUIT_LABEL, type Tone } from '@/utils/labels'

export interface TierTarget {
  target: RouteTarget
  /** Share of this tier's traffic, 0-100. */
  share: number
}

export interface Tier {
  priority: number
  /** 1-based position in the chain. */
  position: number
  targets: TierTarget[]
}

function withShares(targets: RouteTarget[]): TierTarget[] {
  const total = targets.reduce((sum, t) => sum + t.weight, 0)
  return targets.map((target) => ({ target, share: total > 0 ? Math.round((target.weight / total) * 100) : 0 }))
}

/** Lower priority number is tried first; targets with the same priority split traffic by weight. */
export function buildTiers(route: Route, allowed: readonly DataResidency[] | null = null): Tier[] {
  const byPriority = new Map<number, RouteTarget[]>()
  for (const target of route.targets) {
    if (allowed && !allowed.includes(target.residency)) continue
    byPriority.set(target.priority, [...(byPriority.get(target.priority) ?? []), target])
  }
  return [...byPriority.entries()]
    .sort(([a], [b]) => a - b)
    .map(([priority, targets], index) => ({ priority, position: index + 1, targets: withShares(targets) }))
}

export interface TargetStatus {
  label: string
  tone: Tone
  ok: boolean
}

/** Null when no health data is available for the provider. */
export function targetStatus(health: OpsProviderHealth | undefined): TargetStatus | null {
  if (!health) return null
  if (!health.isEnabled) return { label: 'Disabled', tone: 'neutral', ok: false }
  if (health.isDrained) return { label: 'Drained', tone: 'neutral', ok: false }
  const circuit = CIRCUIT_LABEL[health.circuitState]
  return { label: circuit.label, tone: circuit.tone, ok: health.circuitState === 'Closed' }
}

export function routeHasUnhealthyTarget(route: Route, health: Map<string, OpsProviderHealth>): boolean {
  return route.targets.some((t) => targetStatus(health.get(t.providerName))?.ok === false)
}
