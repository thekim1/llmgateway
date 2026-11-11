import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Integration, IntegrationRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

/** Integrations (docs/integrations.md): other systems that use gateway keys for the teams they serve. */
export const useIntegrationsStore = defineStore('integrations', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Integration[]>([])

  const sorted = (list: Integration[]) => [...list].sort((a, b) => a.name.localeCompare(b.name))
  const load = () => run(async () => sorted(await api.integrations.list()))

  async function create(request: IntegrationRequest): Promise<Integration> {
    const created = await api.integrations.create(request)
    items.value = sorted(replaceById(items.value, created))
    return created
  }

  async function update(id: string, request: IntegrationRequest): Promise<Integration> {
    const updated = await api.integrations.update(id, request)
    items.value = sorted(replaceById(items.value, updated))
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.integrations.remove(id)
    items.value = removeById(items.value, id)
  }

  return { items, loading, error, loaded, load, create, update, remove }
})
