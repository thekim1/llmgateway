import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { Budget, BudgetPeriod } from '@/api/types'
import KeyBudgets from './KeyBudgets.vue'

const list = vi.fn()
const create = vi.fn()
const remove = vi.fn()
vi.mock('@/api', () => ({ api: { budgets: { list: (...a: unknown[]) => list(...a), create: (...a: unknown[]) => create(...a), remove: (...a: unknown[]) => remove(...a) } } }))

function budget(period: BudgetPeriod, limitSek: number, spentSek = 0): Budget {
  return {
    id: `b-${period}`, scope: 'VirtualKey', scopeId: 'k1', scopeName: 'Key', limitSek, period, alertThresholds: [80, 100], isActive: true,
    periodStart: '2026-10-01T00:00:00Z', periodEnd: '2026-11-01T00:00:00Z', spentSek, percentUsed: limitSek ? (spentSek / limitSek) * 100 : 0,
  }
}

describe('KeyBudgets', () => {
  beforeEach(() => {
    list.mockReset()
    create.mockReset()
    remove.mockReset()
  })

  it('lists the key’s budgets ordered from shortest to longest period', async () => {
    list.mockResolvedValue([budget('Monthly', 1000, 250), budget('Hourly', 10), budget('Daily', 100)])
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    expect(list).toHaveBeenCalledWith({ scope: 'VirtualKey', scopeId: 'k1' })
    const rows = wrapper.findAll('[data-testid="key-budgets"] li').map((li) => li.text())
    expect(rows[0]).toContain('Hourly')
    expect(rows[1]).toContain('Daily')
    expect(rows[2]).toContain('Monthly')
  })

  it('only offers periods the key does not have a budget for yet', async () => {
    list.mockResolvedValue([budget('Hourly', 10), budget('Monthly', 1000)])
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    const options = wrapper.findAll('select option').map((o) => o.text())
    expect(options).toEqual(['Daily', 'Weekly', 'Quarterly', 'Yearly'])
  })

  it('creates a budget for the chosen period and reloads', async () => {
    list.mockResolvedValueOnce([]).mockResolvedValueOnce([budget('Monthly', 500)])
    create.mockResolvedValue(budget('Monthly', 500))
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    await wrapper.find('input').setValue('500')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith({ scope: 'VirtualKey', scopeId: 'k1', limitSek: 500, period: 'Monthly', alertThresholds: [50, 80, 100], isActive: true })
    expect(list).toHaveBeenCalledTimes(2)
  })

  it('rejects an empty or non-positive limit without calling the API', async () => {
    list.mockResolvedValue([])
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    await wrapper.find('input').setValue('0')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter a limit above 0 kr')
  })

  it('hides editing controls for read-only users', async () => {
    list.mockResolvedValue([budget('Daily', 100)])
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: false } })
    await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.find('button').exists()).toBe(false)
  })

  it('shows the server’s message when a budget cannot be created', async () => {
    list.mockResolvedValue([])
    create.mockRejectedValue(new Error('Det finns redan en budget för samma period.'))
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    await wrapper.find('input').setValue('5')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role=alert]').exists()).toBe(true)
  })

  it('deletes a budget', async () => {
    list.mockResolvedValueOnce([budget('Daily', 100)]).mockResolvedValueOnce([])
    remove.mockResolvedValue(undefined)
    const wrapper = mount(KeyBudgets, { props: { keyId: 'k1', editable: true } })
    await flushPromises()
    await wrapper.find('button[aria-label="Delete Daily budget"]').trigger('click')
    await flushPromises()
    expect(remove).toHaveBeenCalledWith('b-Daily')
  })
})
