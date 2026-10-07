import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Provider, ProviderRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useProvidersStore = defineStore('providers', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Provider[]>([])

  const load = () => run(() => api.providers.list())

  async function create(request: ProviderRequest): Promise<Provider> {
    const created = await api.providers.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: ProviderRequest): Promise<Provider> {
    const updated = await api.providers.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function setDrained(id: string, drained: boolean): Promise<Provider> {
    const updated = await api.providers.drain(id, drained)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.providers.remove(id)
    items.value = removeById(items.value, id)
  }

  return { items, loading, error, loaded, load, create, update, setDrained, remove }
})
