import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import type { Department, Team } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import OrganisationView from './OrganisationView.vue'

const departmentsList = vi.fn()
const departmentsRemove = vi.fn()
const teamsList = vi.fn()
const teamsRemove = vi.fn()
vi.mock('@/api', () => ({
  api: {
    departments: { list: (...a: unknown[]) => departmentsList(...a), remove: (...a: unknown[]) => departmentsRemove(...a) },
    teams: { list: (...a: unknown[]) => teamsList(...a), remove: (...a: unknown[]) => teamsRemove(...a) },
    budgets: { list: () => Promise.resolve([]) },
  },
}))

const rulesProblem = {
  code: 'routing_rules_scoped',
  choices: ['delete', 'deactivate'],
  rules: [
    { id: 'r1', name: 'Premium via header', isEnabled: true },
    { id: 'r2', name: 'Budget guard', isEnabled: true },
  ],
  detail: 'Teamet har 2 routingregel(er). Välj om reglerna ska tas bort eller inaktiveras.',
}

const department = (teamCount: number): Department => ({ id: 'd1', name: 'Kommunstyrelsen', costCenterCode: 'KS', isActive: true, teamCount, createdAt: '2026-01-01T00:00:00Z' })
const team: Team = { id: 't1', departmentId: 'd1', departmentName: 'Kommunstyrelsen', name: 'Servicedesk', description: null, isActive: true, keyCount: 0, createdAt: '2026-01-01T00:00:00Z' }

async function mountView(departments: Department[], teams: Team[]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'Admin', email: null, roles: ['gateway-admin'], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  departmentsList.mockResolvedValue(departments)
  teamsList.mockResolvedValue(teams)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }] })
  const wrapper = mount(OrganisationView, {
    global: { plugins: [pinia, router], stubs: { DepartmentFormDrawer: true, TeamFormDrawer: true, UiDialog: inlineOverlay } },
  })
  await flushPromises()
  return wrapper
}

const teamDelete = (wrapper: Awaited<ReturnType<typeof mountView>>) => wrapper.find('table').findAll('button').find((b) => b.text() === 'Delete')!
const toasts = () => useUiStore().toasts.map((t) => t.message)

beforeEach(() => {
  for (const fn of [departmentsList, departmentsRemove, teamsList, teamsRemove]) fn.mockReset()
})

describe('deleting a team or department', () => {
  it('deletes a team without routing rules in one step', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockResolvedValue(undefined)
    await teamDelete(wrapper).trigger('click')
    expect(wrapper.text()).toContain('Delete team?')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()
    expect(teamsRemove).toHaveBeenCalledTimes(1)
    expect(teamsRemove).toHaveBeenCalledWith('t1', undefined)
    expect(wrapper.text()).not.toContain('routing rule')
    expect(toasts()).toContain('Team deleted')
  })

  it('asks what to do with the team’s routing rules and repeats the delete with the answer (deactivate)', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', rulesProblem)).mockResolvedValueOnce(undefined)
    await teamDelete(wrapper).trigger('click')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()

    // The first confirmation is replaced by the question about the rules.
    expect(wrapper.text()).not.toContain('Delete team?')
    expect(wrapper.text()).toContain('2 routing rules belong to this team')
    expect(wrapper.text()).toContain('Premium via header')
    expect(wrapper.text()).toContain('Budget guard')
    expect(toasts()).not.toContain('Team deleted')
    expect(teamsRemove).toHaveBeenCalledTimes(1)

    await button(wrapper, 'Delete team, deactivate 2 rules').trigger('click')
    await flushPromises()
    expect(teamsRemove).toHaveBeenNthCalledWith(2, 't1', 'deactivate')
    expect(toasts()).toContain('Team deleted. 2 routing rules deactivated: assign a new owner under Routing rules.')
    expect(wrapper.text()).not.toContain('routing rules belong')
  })

  it('can delete the rules together with the team', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', rulesProblem)).mockResolvedValueOnce(undefined)
    await teamDelete(wrapper).trigger('click')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()
    await wrapper.find('input[value="delete"]').setValue(true)
    await button(wrapper, 'Delete team and 2 rules').trigger('click')
    await flushPromises()
    expect(teamsRemove).toHaveBeenNthCalledWith(2, 't1', 'delete')
    expect(toasts()).toContain('Team deleted. 2 routing rules deleted.')
  })

  it('leaves the team alone when the user cancels the question', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', rulesProblem))
    await teamDelete(wrapper).trigger('click')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()
    await button(wrapper, 'Cancel').trigger('click')
    await flushPromises()
    expect(teamsRemove).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).not.toContain('routing rules belong')
    expect(wrapper.find('table').text()).toContain('Servicedesk')
  })

  it('shows an ordinary conflict (the team still has keys) inside the first dialog, without the rules question', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', { detail: 'Teamet har nycklar och kan inte tas bort.' }))
    await teamDelete(wrapper).trigger('click')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Teamet har nycklar')
    expect(wrapper.text()).toContain('Delete team?')
    expect(wrapper.text()).not.toContain('routing rules belong')
  })

  it('keeps the question open with the server’s message if the second delete fails', async () => {
    const wrapper = await mountView([department(1)], [team])
    teamsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', rulesProblem)).mockRejectedValueOnce(new ApiError(409, 'DELETE', { detail: 'Teamet har nycklar och kan inte tas bort.' }))
    await teamDelete(wrapper).trigger('click')
    await button(wrapper, 'Delete team').trigger('click')
    await flushPromises()
    await button(wrapper, 'Delete team, deactivate 2 rules').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('2 routing rules belong to this team')
    expect(wrapper.text()).toContain('Teamet har nycklar')
  })

  it('asks the same question when a department with routing rules is deleted', async () => {
    const wrapper = await mountView([department(0)], [])
    departmentsRemove.mockRejectedValueOnce(new ApiError(409, 'DELETE', { ...rulesProblem, rules: [rulesProblem.rules[0]!] })).mockResolvedValueOnce(undefined)
    await button(wrapper, 'Delete department').trigger('click') // the page's button opens the confirmation
    expect(wrapper.text()).toContain('Delete department?')
    const confirm = wrapper.findAll('button').filter((b) => b.text().includes('Delete department')).at(-1)!
    await confirm.trigger('click')
    await flushPromises()
    // The confirmation is replaced by the rules question, whose button names the choice.
    expect(wrapper.text()).toContain('1 routing rule belongs to this department')
    await button(wrapper, 'Delete department, deactivate 1 rule').trigger('click')
    await flushPromises()
    expect(departmentsRemove).toHaveBeenNthCalledWith(2, 'd1', 'deactivate')
    expect(toasts()).toContain('Department deleted. 1 routing rule deactivated: assign a new owner under Routing rules.')
  })
})
