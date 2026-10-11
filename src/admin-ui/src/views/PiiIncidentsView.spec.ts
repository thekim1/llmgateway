import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { PiiIncident, PiiIncidentReport } from '@/api/types'
import { button } from '@/test/helpers'
import PiiIncidentsView from './PiiIncidentsView.vue'

const piiIncidents = vi.fn()
vi.mock('@/api', () => ({
  api: {
    usage: { piiIncidents: (...a: unknown[]) => piiIncidents(...a) },
    departments: { list: () => Promise.resolve([{ id: 'd1', name: 'Socialtjänst' }]) },
    teams: { list: () => Promise.resolve([{ id: 't1', name: 'Mottagning', departmentId: 'd1' }]) },
    keys: { list: () => Promise.resolve([{ id: 'k1', name: 'Chat app', teamId: 't1', departmentId: 'd1' }]) },
  },
}))

function incident(overrides: Partial<PiiIncident> = {}): PiiIncident {
  return {
    requestId: 'req_1',
    timestamp: '2026-10-11T08:00:00Z',
    keyId: 'k1',
    keyPrefix: 'ume-sk-ab12',
    keyName: 'Chat app',
    teamId: 't1',
    teamName: 'Mottagning',
    departmentId: 'd1',
    departmentName: 'Socialtjänst',
    endpoint: '/v1/chat/completions',
    requestedModel: 'ume/chat',
    providerName: 'azure-se',
    action: 'Redact',
    categories: [
      { category: 'Personnummer', count: 2 },
      { category: 'Email', count: 1 },
    ],
    outcome: 'Success',
    statusCode: 200,
    routingRuleName: null,
    ...overrides,
  }
}

function report(items: PiiIncident[], overrides: Partial<PiiIncidentReport> = {}): PiiIncidentReport {
  return {
    from: '2026-09-11T00:00:00Z',
    to: '2026-10-11T23:59:59Z',
    items,
    total: items.length,
    totalIncidents: items.length,
    categories: [
      { category: 'Personnummer', requests: 2, occurrences: 3 },
      { category: 'Email', requests: 1, occurrences: 1 },
    ],
    actions: [
      { action: 'Redact', requests: 1 },
      { action: 'Block', requests: 1 },
    ],
    ...overrides,
  }
}

async function mountView() {
  setActivePinia(createPinia())
  const wrapper = mount(PiiIncidentsView, { global: { stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return wrapper
}

beforeEach(() => piiIncidents.mockReset())

describe('PiiIncidentsView', () => {
  it('shows tiles per category and action and links each incident to its request', async () => {
    piiIncidents.mockResolvedValue(report([incident(), incident({ requestId: 'req_2', action: 'Block', outcome: 'PiiBlocked', statusCode: 422 })]))
    const wrapper = await mountView()

    expect(wrapper.find('h1').text()).toBe('PII incidents')
    const categories = wrapper.find('[data-testid="category-tiles"]').text()
    expect(categories).toContain('Personal identity number')
    expect(categories).toContain('3 values found')
    expect(wrapper.find('[data-testid="action-tiles"]').text()).toContain('Blocked1')
    const rows = wrapper.findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Personal identity number × 2')
    expect(rows[0]!.text()).toContain('Redacted')
    expect(rows[1]!.text()).toContain('PII blocked')
    expect(wrapper.findAllComponents(RouterLinkStub)[0]!.props('to')).toEqual({ name: 'usage', query: { request: 'req_1' } })
    expect(wrapper.find('[data-testid="pii-explainer"]').exists()).toBe(false)
  })

  it('sends the chosen filters and starts again on page one', async () => {
    piiIncidents.mockResolvedValue(report([incident()]))
    const wrapper = await mountView()
    expect(piiIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 50 }))

    const selects = wrapper.findAll('select')
    await selects[2]!.setValue('k1')
    await selects[3]!.setValue('Personnummer')
    await selects[4]!.setValue('Block')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(piiIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ keyId: 'k1', category: 'Personnummer', action: 'Block', page: 1 }))
  })

  it('explains that scanning is per key when nothing was found', async () => {
    piiIncidents.mockResolvedValue(report([], { categories: [], actions: [] }))
    const wrapper = await mountView()

    expect(wrapper.find('[data-testid="pii-explainer"]').text()).toContain('A key with PII policy Off is not scanned')
    expect(wrapper.text()).toContain('No PII found in this period.')
  })

  it('offers to clear the category and action filters when they hide everything', async () => {
    piiIncidents.mockResolvedValue(report([], { totalIncidents: 2 }))
    const wrapper = await mountView()
    await wrapper.findAll('select')[4]!.setValue('RerouteToOnPrem')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('No incidents match these filters.')

    await button(wrapper, 'Clear filters').trigger('click')
    await flushPromises()
    expect(piiIncidents).toHaveBeenLastCalledWith(expect.not.objectContaining({ action: expect.anything() }))
  })
})
