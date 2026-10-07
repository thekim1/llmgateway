import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Route, RouteRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useRoutesStore = defineStore('routes', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Route[]>([])

  const load = () => run(() => api.routes.list())

  async function create(request: RouteRequest): Promise<Route> {
    const created = await api.routes.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: RouteRequest): Promise<Route> {
    const updated = await api.routes.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.routes.remove(id)
    items.value = removeById(items.value, id)
  }

  return { items, loading, error, loaded, load, create, update, remove }
})
