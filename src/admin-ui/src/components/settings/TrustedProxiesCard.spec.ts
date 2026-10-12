import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { NetworkSettings } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import TrustedProxiesCard from './TrustedProxiesCard.vue'

const network = vi.fn()
const updateNetwork = vi.fn()
vi.mock('@/api', () => ({
  api: {
    settings: {
      network: (...a: unknown[]) => network(...a),
      updateNetwork: (...a: unknown[]) => updateNetwork(...a),
    },
  },
}))

const settings = (overrides: Partial<NetworkSettings> = {}): NetworkSettings => ({
  trustedProxies: [],
  updatedAt: null,
  observed: { remoteAddress: '10.0.0.5', forwardedFor: '203.0.113.7' },
  ...overrides,
})

async function mountCard(initial: NetworkSettings) {
  setActivePinia(createPinia())
  network.mockResolvedValue(initial)
  const wrapper = mount(TrustedProxiesCard, { global: { stubs: { UiDialog: inlineOverlay } } })
  await flushPromises()
  return wrapper
}

const textarea = (wrapper: Awaited<ReturnType<typeof mountCard>>) => wrapper.find('textarea#trusted-proxies')

beforeEach(() => {
  network.mockReset()
  updateNetwork.mockReset()
})

describe('TrustedProxiesCard', () => {
  it('shows the saved proxies and the connection the admin API saw', async () => {
    const wrapper = await mountCard(settings({ trustedProxies: ['10.0.0.5', '10.0.1.0/28'], updatedAt: '2026-10-12T08:00:00Z' }))
    expect((textarea(wrapper).element as HTMLTextAreaElement).value).toBe('10.0.0.5\n10.0.1.0/28')
    expect(wrapper.text()).toContain('X-Forwarded-For: 203.0.113.7')
    expect(wrapper.text()).toContain('Last changed')
    // Already listed: no shortcut to add it again.
    expect(wrapper.findAll('button').some((b) => b.text() === 'Add 10.0.0.5')).toBe(false)
  })

  it('adds the observed proxy address and saves after confirmation', async () => {
    const wrapper = await mountCard(settings())
    await button(wrapper, 'Add 10.0.0.5').trigger('click')
    expect((textarea(wrapper).element as HTMLTextAreaElement).value).toBe('10.0.0.5')

    updateNetwork.mockResolvedValue(settings({ trustedProxies: ['10.0.0.5'], updatedAt: '2026-10-12T08:00:00Z' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Only list proxies you operate')
    await button(wrapper, 'Save proxies').trigger('click')
    await flushPromises()
    expect(updateNetwork).toHaveBeenCalledWith(['10.0.0.5'])
  })

  it('warns that clearing the list ignores the proxy header', async () => {
    const wrapper = await mountCard(settings({ trustedProxies: ['10.0.0.5'] }))
    await textarea(wrapper).setValue('')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('ignore X-Forwarded-For')
  })

  it.each([
    ['0.0.0.0/0', 'trust every caller'],
    ['10.0.0.5/24', 'Did you mean 10.0.0.0/24?'],
    ['proxy.example.se', 'not an IP address'],
  ])('refuses %s before asking for confirmation', async (entry, message) => {
    const wrapper = await mountCard(settings())
    await textarea(wrapper).setValue(entry)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain(message)
    expect(wrapper.text()).not.toContain('Change trusted proxies?')
    expect(updateNetwork).not.toHaveBeenCalled()
  })
})
