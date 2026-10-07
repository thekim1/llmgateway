import { defineStore } from 'pinia'
import { api } from '@/api'
import type { CreateModelRequest, Model, NewPrice, Price, UpdateModelRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useModelsStore = defineStore('models', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Model[]>([])

  const load = () => run(() => api.models.list())

  async function create(request: CreateModelRequest): Promise<Model> {
    const created = await api.models.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: UpdateModelRequest): Promise<Model> {
    const updated = await api.models.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.models.remove(id)
    items.value = removeById(items.value, id)
  }

  const prices = (id: string): Promise<Price[]> => api.models.prices(id)

  async function addPrice(id: string, price: NewPrice): Promise<Price> {
    const created = await api.models.addPrice(id, price)
    await load()
    return created
  }

  return { items, loading, error, loaded, load, create, update, remove, prices, addPrice }
})
