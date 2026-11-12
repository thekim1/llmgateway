import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Integration } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import IntegrationsView from './IntegrationsView.vue'

const list = vi.fn()
const create = vi.fn()
const update = vi.fn()
const remove = vi.fn()
vi.mock('@/api', () => ({
  api: {
    integrations: {
      list: (...a: unknown[]) => list(...a),
      create: (...a: unknown[]) => create(...a),
      update: (...a: unknown[]) => update(...a),
      remove: (...a: unknown[]) => remove(...a),
    },
    teams: { list: () => Promise.resolve([{ id: 't1', name: 'Platform', departmentName: 'IT' }]) },
  },
}))

const integration = (overrides: Partial<Integration> = {}): Integration => ({
  id: 'i1',
  name: 'Ume.Rag',
  description: 'RAG platform',
  ownerTeamId: 't1',
  ownerTeamName: 'Platform',
  ownerDepartmentId: 'd1',
  ownerDepartmentName: 'IT',
  isActive: true,
  createdAt: '2026-11-01T08:00:00Z',
  createdBy: 'admin',
  credentialKeys: 2,
  assignedKeys: 5,
  ...overrides,
})

async function mountView(items: Integration[]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'Admin', email: null, roles: ['gateway-admin'], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  list.mockResolvedValue(items)
  const wrapper = mount(IntegrationsView, { global: { plugins: [pinia], stubs: { UiDialog: inlineOverlay, UiDrawer: inlineOverlay } } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  for (const fn of [list, create, update, remove]) fn.mockReset()
})

describe('IntegrationsView', () => {
  it('lists integrations with owner team, key counts and status', async () => {
    const wrapper = await mountView([integration(), integration({ id: 'i2', name: 'Agents', isActive: false, credentialKeys: 1, assignedKeys: 0 })])
    const text = wrapper.text()
    expect(text).toContain('Ume.Rag')
    expect(text).toContain('Platform')
    expect(text).toContain('2 credential keys')
    expect(text).toContain('5 assigned keys')
    expect(text).toContain('1 credential key')
    expect(text).toContain('Inactive')
  })

  it('creates an integration with its owner team', async () => {
    create.mockResolvedValue(integration({ id: 'i3', name: 'Agent platform', credentialKeys: 0, assignedKeys: 0 }))
    const wrapper = await mountView([])
    await button(wrapper, 'New integration').trigger('click')
    await flushPromises()
    await wrapper.find('#integration-name').setValue(' Agent platform ')
    await wrapper.find('#integration-owner').setValue('t1')
    await wrapper.find('form#integration-form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith({ name: 'Agent platform', description: null, ownerTeamId: 't1', isActive: true })
    expect(wrapper.text()).toContain('Agent platform')
  })

  it('asks for a name and an owner team before sending', async () => {
    const wrapper = await mountView([])
    await button(wrapper, 'New integration').trigger('click')
    await flushPromises()
    await wrapper.find('form#integration-form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Choose the team that runs the integration.')
  })
})
