import { defineStore } from 'pinia'
import { api } from '@/api'
import type { CreateDepartmentRequest, Department, RoutingRulesChoice, UpdateDepartmentRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useDepartmentsStore = defineStore('departments', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Department[]>([])

  const load = () => run(() => api.departments.list())

  async function create(request: CreateDepartmentRequest): Promise<Department> {
    const created = await api.departments.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: UpdateDepartmentRequest): Promise<Department> {
    const updated = await api.departments.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string, routingRules?: RoutingRulesChoice): Promise<void> {
    await api.departments.remove(id, routingRules)
    items.value = removeById(items.value, id)
  }

  return { items, loading, error, loaded, load, create, update, remove }
})
