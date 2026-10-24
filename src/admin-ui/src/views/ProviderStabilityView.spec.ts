import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { ProviderStability } from '@/api/types'
import { button } from '@/test/helpers'
import ProviderStabilityView from './ProviderStabilityView.vue'

const providerStats = vi.fn()
vi.mock('@/api', () => ({
  api: { ops: { providerStats: (...a: unknown[]) => providerStats(...a) } },
}))

const stats: ProviderStability = {
  from: '2026-10-10T10:00:00Z',
  to: '2026-10-10T13:00:00Z',
  bucket: 'Hour',
  timeZone: 'UTC',
  providers: [
    { providerId: 'b', name: 'onprem-llm', exists: true, requests: 40, providerErrors: 0, availability: 1, fallbackRate: 0, p50FirstTokenMs: 300, p95FirstTokenMs: 900 },
    { providerId: 'a', name: 'azure-se', exists: true, requests: 100, providerErrors: 10, availability: 0.9, fallbackRate: 0.05, p50FirstTokenMs: 800, p95FirstTokenMs: 4500 },
  ],
  buckets: [
    { providerId: 'a', start: '2026-10-10T10:00:00Z', requests: 50, providerErrors: 0, errorRate: 0, fallbacks: 0, p50FirstTokenMs: 700, p95FirstTokenMs: 1200 },
    { providerId: 'a', start: '2026-10-10T12:00:00Z', requests: 50, providerErrors: 10, errorRate: 0.2, fallbacks: 5, p50FirstTokenMs: 900, p95FirstTokenMs: 4500 },
    { providerId: 'b', start: '2026-10-10T11:00:00Z', requests: 40, providerErrors: 0, errorRate: 0, fallbacks: 0, p50FirstTokenMs: 300, p95FirstTokenMs: 900 },
  ],
}

async function mountView() {
  setActivePinia(createPinia())
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/operations', name: 'ops', component: { template: '<div />' } },
    ],
  })
  const wrapper = mount(ProviderStabilityView, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  providerStats.mockReset()
  providerStats.mockResolvedValue(stats)
})

describe('ProviderStabilityView', () => {
  it('ranks the most stable provider first and describes each chart in text', async () => {
    const wrapper = await mountView()
    expect(providerStats).toHaveBeenCalledWith(expect.objectContaining({ bucket: 'hour' }))

    const rows = wrapper.find('table').findAll('tbody tr')
    expect(rows.map((r) => r.text())).toEqual([expect.stringContaining('onprem-llm'), expect.stringContaining('azure-se')])
    expect(rows[1]!.text()).toContain('90%')

    const charts = wrapper.findAll('svg[role="img"]')
    expect(charts).toHaveLength(2)
    const firstToken = charts[0]!.attributes('aria-label')!
    expect(firstToken).toContain('p95 time to first token per provider, 3 periods')
    expect(firstToken).toContain('azure-se: highest 4,5 s')
    expect(charts[1]!.attributes('aria-label')).toContain('azure-se: highest 20%')
  })

  it('draws a gap where a provider had no traffic and keeps colours by provider name', async () => {
    const wrapper = await mountView()
    const paths = wrapper.findAll('svg[role="img"]')[0]!.findAll('path')
    // azure-se sorts first, so it gets the first colour although it ranks second.
    expect(paths[0]!.attributes('stroke')).toBe('var(--series-1)')
    // Two separate points (10:00 and 12:00, nothing at 11:00) give two dots, not one line.
    expect(paths[0]!.attributes('d')!.match(/M/g)).toHaveLength(2)
    expect(wrapper.text()).toContain('azure-se')
    expect(wrapper.find('[aria-label="p95 time to first token legend"]').text()).toContain('onprem-llm')
  })

  it('offers each chart as a table', async () => {
    const wrapper = await mountView()
    await button(wrapper, 'View as table').trigger('click')
    const table = wrapper.findAll('table').find((t) => t.find('caption').text() === 'p95 time to first token per provider')!
    expect(table.findAll('tbody tr')).toHaveLength(3)
    expect(table.findAll('tbody tr')[1]!.text()).toContain('—')
  })

  it('asks for day buckets for 30 days', async () => {
    const wrapper = await mountView()
    await button(wrapper, '30 days').trigger('click')
    await flushPromises()
    expect(providerStats).toHaveBeenLastCalledWith(expect.objectContaining({ bucket: 'day' }))
  })
})
