import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { UsageMetrics, UsageMetricsBucket } from '@/api/types'
import { button } from '@/test/helpers'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useTeamsStore } from '@/stores/teams'
import UsageDashboard from './UsageDashboard.vue'

const metrics = vi.fn()
vi.mock('@/api', () => ({
  api: {
    usage: { metrics: (...a: unknown[]) => metrics(...a) },
    departments: { list: () => Promise.resolve([{ id: 'd1', name: 'Socialtjänst' }]) },
    teams: { list: () => Promise.resolve([{ id: 't1', name: 'Mottagning', departmentId: 'd1' }]) },
    keys: { list: () => Promise.resolve([{ id: 'k1', name: 'Chat app', teamId: 't1', departmentId: 'd1' }]) },
    catalog: { get: () => Promise.resolve({ baseUrl: '', models: [{ name: 'eu/gpt', residency: 'Eu' }], routes: [] }) },
  },
}))

const stats = (avg: number | null) => ({ avg, p50: avg, p90: avg, p95: avg, p99: avg })
function bucket(start: string, requests: number): UsageMetricsBucket {
  return {
    start, requests, successes: requests, providerErrors: 0, refused: 0, cancelled: 0, fallbacks: 0, costUsd: requests / 100,
    inputTokens: requests * 10, cachedInputTokens: requests * 4, outputTokens: requests * 2,
    latency: stats(requests ? 1200 : null), overhead: stats(requests ? 9 : null), firstTokenAvgMs: null, firstTokenP95Ms: null, tokensPerSecond: requests ? 40 : null,
  }
}
function data(overrides: Partial<UsageMetrics> = {}): UsageMetrics {
  return {
    from: '2026-10-06T00:00:00Z', to: '2026-10-13T00:00:00Z', bucket: 'day', timeZone: 'Europe/Stockholm', recordsFrom: null,
    totals: {
      requests: 30, successes: 29, providerErrors: 1, refused: 0, cancelled: 0, fallbacks: 3, costUsd: 0.3, inputTokens: 300, cachedInputTokens: 120,
      outputTokens: 60, cacheHitRate: 0.4, latency: stats(1200), overhead: stats(9), firstToken: { avg: null, p50: null, p95: null }, tokensPerSecond: 40,
    },
    buckets: [bucket('2026-10-11T22:00:00Z', 10), bucket('2026-10-12T22:00:00Z', 20)],
    models: [{ model: 'eu/gpt', requests: 30, costUsd: 0.3, requestsPerBucket: [10, 20], costUsdPerBucket: [0.1, 0.2] }],
    ...overrides,
  }
}

async function mountDashboard() {
  setActivePinia(createPinia())
  // The Usage page loads the organisation for both of its tabs.
  await Promise.all([useDepartmentsStore().load(), useTeamsStore().load(), useKeysStore().load()])
  const wrapper = mount(UsageDashboard)
  await flushPromises()
  return wrapper
}

beforeEach(() => metrics.mockReset())

describe('UsageDashboard', () => {
  it('loads the last seven days and shows the headline of every card', async () => {
    metrics.mockResolvedValue(data())
    const wrapper = await mountDashboard()

    const query = metrics.mock.lastCall![0] as { from: string; to: string }
    expect(new Date(query.to).getTime() - new Date(query.from).getTime()).toBe(7 * 86_400_000)
    const titles = wrapper.findAll('h3').map((h) => h.text())
    expect(titles).toEqual(['Cost', 'Requests', 'Tokens', 'Provider cache hit rate', 'Requests by model', 'Latency', 'Gateway overhead', 'Time to first token', 'Throughput'])
    expect(wrapper.text()).toMatch(/Total 0,30\sUS\$/)
    expect(wrapper.text()).toContain('3,3% errors · 10% fell back')
    expect(wrapper.find('[role="meter"]').attributes('aria-valuenow')).toBe('40')
    expect(wrapper.text()).toContain('No streamed answers in this period.')
  })

  it('reloads at once when a filter or the time range changes, and clears the filters', async () => {
    metrics.mockResolvedValue(data())
    const wrapper = await mountDashboard()
    const selects = wrapper.findAll('select')
    await selects[0]!.setValue('d1')
    await flushPromises()
    expect(metrics).toHaveBeenLastCalledWith(expect.objectContaining({ departmentId: 'd1' }))

    await button(wrapper, '24 hours').trigger('click')
    await flushPromises()
    const query = metrics.mock.lastCall![0] as { from: string; to: string }
    expect(new Date(query.to).getTime() - new Date(query.from).getTime()).toBe(86_400_000)

    await button(wrapper, 'Clear filters').trigger('click')
    await flushPromises()
    expect(metrics).toHaveBeenLastCalledWith(expect.not.objectContaining({ departmentId: expect.anything() }))
  })

  it('says where the charts start when older usage is only kept as monthly totals', async () => {
    metrics.mockResolvedValue(data({ recordsFrom: '2026-10-01T00:00:00Z' }))
    const wrapper = await mountDashboard()
    expect(wrapper.text()).toContain('The charts start on 1 Oct 2026')
  })

  it('switches a card to its table view', async () => {
    metrics.mockResolvedValue(data())
    const wrapper = await mountDashboard()
    await wrapper.findAll('button[aria-label="Table"]')[0]!.trigger('click')
    expect(wrapper.find('table caption').text()).toBe('Cost')
  })
})
