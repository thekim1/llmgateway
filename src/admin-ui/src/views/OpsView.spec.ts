import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { api } from '@/api'
import type { OpsHealth } from '@/api/types'
import { createAppI18n } from '@/i18n'
import OpsView from './OpsView.vue'

afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = '' })

describe('live operations status', () => {
  it.each([true, false])('renders live metadata or explicit unknown state: available=%s', async available => {
    const health: OpsHealth = {
      checkedAt: '2026-10-07T06:00:00Z',
      components: [{ name: 'gateway', status: available ? 'Healthy' : 'Unhealthy', description: 'Status' }],
      versions: { adminApi: '0.1.0', gateway: available ? '9.8.7' : null, schema: 'InitialCreate' },
      usageWriter: available ? { queueDepth: 17, capacity: 10000, inFlightRecords: 5, lastWriteAt: null, consecutiveFailures: 0 } : null,
      providers: [],
    }
    vi.spyOn(api.ops, 'health').mockResolvedValue(health)
    const wrapper = mount(OpsView, { attachTo: document.body, global: { plugins: [createPinia(), createAppI18n()] } })
    await flushPromises()
    expect(wrapper.text()).toContain('Lagring av användning')
    if (available) {
      expect(wrapper.text()).toContain('9.8.7')
      expect(wrapper.text()).toContain('17 / 10000')
      expect(wrapper.text()).toContain('Inga poster har skrivits ännu.')
    } else {
      expect(wrapper.text()).toContain('Uppgiften kunde inte hämtas.')
      expect(wrapper.text()).not.toContain('0 / 10000')
    }
    wrapper.unmount()
  })
})
