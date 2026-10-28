import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { ModelPriceSync } from '@/api/types'
import ModelPricesCard from './ModelPricesCard.vue'

const modelPrices = vi.fn()
const runModelPrices = vi.fn()
vi.mock('@/api', () => ({
  api: { settings: { modelPrices: (...a: unknown[]) => modelPrices(...a), runModelPrices: (...a: unknown[]) => runModelPrices(...a) } },
}))

const status = (overrides: Partial<ModelPriceSync> = {}): ModelPriceSync => ({
  enabled: true,
  priceListUrl: 'https://models.dev/api.json',
  lastRunAt: '2026-10-28T08:00:00Z',
  followed: 12,
  updated: 2,
  enriched: 1,
  error: null,
  nextRunAt: '2026-10-29T08:00:00Z',
  running: false,
  ...overrides,
})

async function mountCard(initial: ModelPriceSync) {
  setActivePinia(createPinia())
  modelPrices.mockResolvedValue(initial)
  const wrapper = mount(ModelPricesCard)
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  modelPrices.mockReset()
  runModelPrices.mockReset()
})

describe('ModelPricesCard', () => {
  it('shows the last check', async () => {
    const wrapper = await mountCard(status())
    expect(wrapper.text()).toContain('12 models follow the list, 2 prices updated')
    expect(wrapper.text()).toContain('1 models given details')
    expect(wrapper.text()).toContain('Next check')
  })

  it('shows a failed check', async () => {
    const wrapper = await mountCard(status({ error: 'The price list answered 503.' }))
    expect(wrapper.text()).toContain('The price list answered 503.')
    expect(wrapper.text()).toContain('Prices are unchanged')
  })

  it('explains when updates are switched off and offers no check', async () => {
    const wrapper = await mountCard(status({ enabled: false, lastRunAt: null, nextRunAt: null }))
    expect(wrapper.text()).toContain('Admin__AutoUpdatePrices')
    expect(wrapper.text()).not.toContain('Check now')
  })

  it('starts a check', async () => {
    const wrapper = await mountCard(status())
    runModelPrices.mockResolvedValue(status())
    await wrapper.findAll('button').find((b) => b.text().includes('Check now'))!.trigger('click')
    await flushPromises()
    expect(runModelPrices).toHaveBeenCalledOnce()
    expect(wrapper.text()).toContain('Checking now')
    wrapper.unmount()
  })
})
