import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { RetentionSettings } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import RetentionCard from './RetentionCard.vue'

const retention = vi.fn()
const updateRetention = vi.fn()
const runRetention = vi.fn()
vi.mock('@/api', () => ({
  api: {
    settings: {
      retention: (...a: unknown[]) => retention(...a),
      updateRetention: (...a: unknown[]) => updateRetention(...a),
      runRetention: (...a: unknown[]) => runRetention(...a),
    },
  },
}))

const settings = (overrides: Partial<RetentionSettings> = {}): RetentionSettings => ({
  usageRetentionDays: 365,
  auditRetentionDays: 365,
  usageRetainedFrom: null,
  nextUsageCutoff: '2025-10-01T00:00:00+02:00',
  nextAuditCutoff: '2025-10-12T00:00:00Z',
  enabled: true,
  nextRunAt: '2026-10-13T01:00:00Z',
  running: false,
  lastRunAt: null,
  lastRunDeleted: null,
  lastRunError: null,
  ...overrides,
})

async function mountCard(initial: RetentionSettings) {
  setActivePinia(createPinia())
  retention.mockResolvedValue(initial)
  const wrapper = mount(RetentionCard, { global: { stubs: { UiDialog: inlineOverlay } } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  for (const fn of [retention, updateRetention, runRetention]) fn.mockReset()
})

describe('RetentionCard', () => {
  it('shows the settings, what will be removed and the last run', async () => {
    const wrapper = await mountCard(settings({ usageRetainedFrom: '2025-09-01T00:00:00+02:00', lastRunAt: '2026-10-12T01:00:00Z', lastRunDeleted: 1234 }))
    expect((wrapper.find('#retention-usage').element as HTMLInputElement).value).toBe('365')
    expect((wrapper.find('#retention-audit').element as HTMLInputElement).value).toBe('365')
    expect(wrapper.text()).toContain('Usage records are kept from')
    expect(wrapper.text()).toMatch(/1\s?234 rows removed/)
    expect(wrapper.text()).toContain('Next run')
    expect(wrapper.text()).toContain('Kept as monthly totals')
  })

  it('saves both periods after confirmation', async () => {
    const wrapper = await mountCard(settings())
    await wrapper.find('#retention-usage').setValue('180')
    await wrapper.find('#retention-audit').setValue('730')
    updateRetention.mockResolvedValue(settings({ usageRetentionDays: 180, auditRetentionDays: 730 }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('older than 180 days')
    await button(wrapper, 'Save retention').trigger('click')
    await flushPromises()
    expect(updateRetention).toHaveBeenCalledWith(180, 730)
  })

  it('keeps the audit log for ever when ticked', async () => {
    const wrapper = await mountCard(settings())
    await wrapper.findAll('label').find((l) => l.text().startsWith('Keep the audit log for ever'))!.find('input').setValue(true)
    expect(wrapper.find('#retention-audit').exists()).toBe(false)
    updateRetention.mockResolvedValue(settings({ auditRetentionDays: null, nextAuditCutoff: null }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Audit entries are kept for ever.')
    await button(wrapper, 'Save retention').trigger('click')
    await flushPromises()
    expect(updateRetention).toHaveBeenCalledWith(365, null)
  })

  it.each(['30', '4000', 'abc'])('refuses %s days before asking', async (days) => {
    const wrapper = await mountCard(settings())
    await wrapper.find('#retention-usage').setValue(days)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Enter a whole number of days from 90')
    expect(wrapper.text()).not.toContain('Change data retention?')
  })

  it('starts a run and shows it as running', async () => {
    const wrapper = await mountCard(settings())
    runRetention.mockResolvedValue(settings())
    await button(wrapper, 'Run now').trigger('click')
    await flushPromises()
    expect(runRetention).toHaveBeenCalled()
    expect(wrapper.text()).toContain('Running now')
  })

  it('shows a failed run', async () => {
    const wrapper = await mountCard(settings({ lastRunAt: '2026-10-12T01:00:00Z', lastRunError: 'PostgresException (SQL state 42501)' }))
    expect(wrapper.text()).toContain('failed (PostgresException (SQL state 42501))')
  })
})
