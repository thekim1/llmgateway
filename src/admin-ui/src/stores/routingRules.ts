import { defineStore } from 'pinia'
import { api } from '@/api'
import type { RoutingRule, RoutingRuleReassignRequest, RoutingRuleRequest, RoutingScope } from '@/api/types'
import { toRequest } from '@/utils/routingRules'
import { removeById, replaceById, useLoadable } from './loadable'

export const useRoutingRulesStore = defineStore('routingRules', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<RoutingRule[]>([])

  const load = () => run(() => api.routingRules.list())

  async function create(request: RoutingRuleRequest): Promise<RoutingRule> {
    const created = await api.routingRules.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: RoutingRuleRequest): Promise<RoutingRule> {
    const updated = await api.routingRules.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function setEnabled(rule: RoutingRule, isEnabled: boolean): Promise<RoutingRule> {
    return update(rule.id, toRequest(rule, { isEnabled }))
  }

  async function remove(id: string): Promise<void> {
    await api.routingRules.remove(id)
    items.value = removeById(items.value, id)
  }

  async function reassign(id: string, request: RoutingRuleReassignRequest): Promise<RoutingRule> {
    const updated = await api.routingRules.reassign(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  /** Saves a new checking order for one scope and owner; the server answers with the rules and their new priorities. */
  async function reorder(scope: RoutingScope, scopeId: string | null, ruleIds: string[]): Promise<void> {
    const reordered = await api.routingRules.reorder({ scope, scopeId, ruleIds })
    for (const rule of reordered) items.value = replaceById(items.value, rule)
  }

  return { items, loading, error, loaded, load, create, update, setEnabled, remove, reassign, reorder }
})
