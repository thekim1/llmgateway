import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { DataApiKey } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useDataAccessStore } from '@/stores/dataAccess'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import DataAccessView from './DataAccessView.vue'

const list = vi.fn()
const settings = vi.fn()
const create = vi.fn()
const revoke = vi.fn()
const remove = vi.fn()
vi.mock('@/api', () => ({
  api: {
    dataAccess: {
      list: (...a: unknown[]) => list(...a),
      settings: (...a: unknown[]) => settings(...a),
      create: (...a: unknown[]) => create(...a),
      update: vi.fn(),
      revoke: (...a: unknown[]) => revoke(...a),
      remove: (...a: unknown[]) => remove(...a),
    },
  },
}))

const key = (overrides: Partial<DataApiKey> = {}): DataApiKey => ({
  id: 'k1',
  name: 'Power BI finance',
  description: null,
  prefix: 'ume-dk-Ab12',
  permissions: ['usage.aggregate'],
  status: 'Active',
  createdAt: '2026-10-01T08:00:00Z',
  createdBy: 'admin',
  expiresAt: null,
  revokedAt: null,
  lastUsedAt: null,
  ...overrides,
})

async function mountView(keys: DataApiKey[]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'Admin', email: null, roles: ['gateway-admin'], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  list.mockResolvedValue(keys)
  settings.mockResolvedValue({ dataApiUrl: 'https://data.example.se:10443', permissions: ['usage.aggregate', 'usage.detail', 'security.read', 'catalog.read'] })
  const wrapper = mount(DataAccessView, { global: { plugins: [pinia], stubs: { UiDialog: inlineOverlay, UiDrawer: inlineOverlay } } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  for (const fn of [list, settings, create, revoke, remove]) fn.mockReset()
})

describe('Data API access', () => {
  it('lists keys with their data access and offers revoke for active and delete for revoked keys', async () => {
    const wrapper = await mountView([key(), key({ id: 'k2', name: 'SIEM', permissions: ['security.read', 'catalog.read'], status: 'Revoked', revokedAt: '2026-10-02T08:00:00Z' })])
    const rows = wrapper.findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Usage totals')
    const buttons = (i: number) => rows[i]!.findAll('button').map((b) => b.text())
    expect(buttons(0).join(' ')).toContain('Revoke')
    expect(rows[1]!.text()).toContain('Security data')
    expect(rows[1]!.text()).toContain('Catalogue')
    expect(buttons(1).join(' ')).toContain('Delete')
    expect(buttons(1).join(' ')).not.toContain('Revoke')
    expect(wrapper.text()).toContain('https://data.example.se:10443')
  })

  it('creates a key with the ticked permissions and shows the secret once with an example request', async () => {
    const wrapper = await mountView([])
    create.mockResolvedValue({ key: key({ permissions: ['usage.detail', 'security.read'] }), secret: 'ume-dk-secretvalue' })
    await button(wrapper, 'New Data API key').trigger('click')
    await wrapper.find('#data-key-name').setValue('Power BI finance')
    for (const label of ['Security data', 'Usage records']) {
      await wrapper.findAll('label').find((l) => l.text().startsWith(label))!.find('input').setValue(true)
    }
    await wrapper.find('form#data-key-form').trigger('submit')
    await flushPromises()

    // Sent in the canonical order, not the order they were ticked in.
    expect(create).toHaveBeenCalledWith({ name: 'Power BI finance', description: null, permissions: ['usage.detail', 'security.read'], expiresAt: null })
    expect(wrapper.find('[data-testid="secret"]').text()).toBe('ume-dk-secretvalue')
    expect(wrapper.text()).toContain('https://data.example.se:10443/v1/usage/records?limit=100')
    expect(wrapper.text()).toContain('$UME_DATA_KEY')
    expect(JSON.stringify(useDataAccessStore().items)).not.toContain('ume-dk-secretvalue')
  })

  it('requires a name and at least one permission', async () => {
    const wrapper = await mountView([])
    await button(wrapper, 'New Data API key').trigger('click')
    await wrapper.find('form#data-key-form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Choose at least one kind of data.')
    expect(wrapper.text()).toContain('Name the integration')
  })

  it('revokes a key after confirmation', async () => {
    const wrapper = await mountView([key()])
    revoke.mockResolvedValue(key({ status: 'Revoked', revokedAt: '2026-10-11T08:00:00Z' }))
    await button(wrapper, 'Revoke').trigger('click')
    expect(wrapper.text()).toContain('Revoke Power BI finance?')
    await button(wrapper, 'Revoke key').trigger('click')
    await flushPromises()
    expect(revoke).toHaveBeenCalledWith('k1')
    expect(wrapper.find('tbody tr').text()).toContain('Delete')
  })
})
