import { defineStore } from 'pinia'
import { api } from '@/api'
import type { CreateTeamRequest, Team, UpdateTeamRequest } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useTeamsStore = defineStore('teams', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Team[]>([])

  const load = (departmentId?: string) => run(() => api.teams.list(departmentId))

  async function create(request: CreateTeamRequest): Promise<Team> {
    const created = await api.teams.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: UpdateTeamRequest): Promise<Team> {
    const updated = await api.teams.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.teams.remove(id)
    items.value = removeById(items.value, id)
  }

  return { items, loading, error, loaded, load, create, update, remove }
})
