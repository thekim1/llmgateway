import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { RequestLogSettings } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import RequestLogCard from './RequestLogCard.vue'

const requestLog = vi.fn()
const updateRequestLog = vi.fn()
vi.mock('@/api', () => ({
  api: {
    settings: {
      requestLog: (...a: unknown[]) => requestLog(...a),
      updateRequestLog: (...a: unknown[]) => updateRequestLog(...a),
    },
  },
}))

const settings = (overrides: Partial<RequestLogSettings> = {}): RequestLogSettings => ({
  active: false,
  until: null,
  updatedAt: null,
  ...overrides,
})

async function mountCard(initial: RequestLogSettings) {
  setActivePinia(createPinia())
  requestLog.mockResolvedValue(initial)
  const wrapper = mount(RequestLogCard, { global: { stubs: { UiDialog: inlineOverlay, RouterLink: { template: '<a><slot /></a>' } } } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  requestLog.mockReset()
  updateRequestLog.mockReset()
})

describe('RequestLogCard', () => {
  it('explains that every request is recorded while off', async () => {
    const wrapper = await mountCard(settings())
    expect(wrapper.text()).toContain('Off: only refused, failed and cancelled requests are logged')
    expect(wrapper.text()).toContain('Every request is recorded either way.')
    expect(wrapper.text()).toContain('Monitor › Usage')
  })

  it('turns on for 24 hours after confirming the cost', async () => {
    const wrapper = await mountCard(settings())
    await button(wrapper, 'Log every request…').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('for the next 24 hours')
    expect(wrapper.text()).toContain('6–14 %')

    updateRequestLog.mockResolvedValue(settings({ active: true, until: '2026-10-16T08:00:00Z', updatedAt: '2026-10-15T08:00:00Z' }))
    await button(wrapper, 'Turn on').trigger('click')
    await flushPromises()
    expect(updateRequestLog).toHaveBeenCalledWith(true, false)
    expect(wrapper.text()).toContain('On until')
  })

  it('turns on without a time limit', async () => {
    const wrapper = await mountCard(settings())
    await button(wrapper, 'No time limit').trigger('click')
    await button(wrapper, 'Log every request…').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('until someone turns this off')

    updateRequestLog.mockResolvedValue(settings({ active: true }))
    await button(wrapper, 'Turn on').trigger('click')
    await flushPromises()
    expect(updateRequestLog).toHaveBeenCalledWith(true, true)
    expect(wrapper.text()).toContain('On, no time limit')
  })

  it('turns off', async () => {
    const wrapper = await mountCard(settings({ active: true }))
    await button(wrapper, 'Turn off…').trigger('click')
    await flushPromises()
    updateRequestLog.mockResolvedValue(settings())
    // The dialog's button; the card's own is "Turn off…".
    await wrapper.findAll('button').find((b) => b.text() === 'Turn off')!.trigger('click')
    await flushPromises()
    expect(updateRequestLog).toHaveBeenCalledWith(false, false)
    expect(wrapper.text()).toContain('Off: only refused')
  })
})
