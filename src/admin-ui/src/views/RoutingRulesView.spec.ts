import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ApiError } from '@/api/client'
import type { RoutingRule } from '@/api/types'
import { useUiStore } from '@/stores/ui'
import { inlineOverlay, rule } from '@/test/fixtures'
import { button, buttonByLabel } from '@/test/helpers'
import RoutingRulesView from './RoutingRulesView.vue'

const list = vi.fn()
const update = vi.fn()
const remove = vi.fn()
const reorder = vi.fn()
vi.mock('@/api', () => ({
  api: {
    routingRules: {
      list: (...a: unknown[]) => list(...a),
      update: (...a: unknown[]) => update(...a),
      remove: (...a: unknown[]) => remove(...a),
      reorder: (...a: unknown[]) => reorder(...a),
    },
  },
}))

let first: RoutingRule
let second: RoutingRule
let global: RoutingRule
let orphan: RoutingRule

async function mountView(rules: RoutingRule[]) {
  setActivePinia(createPinia())
  list.mockResolvedValue(rules)
  const wrapper = mount(RoutingRulesView, {
    global: { stubs: { RuleFormDrawer: true, ReassignRuleDialog: true, RuleTestDialog: true, UiDialog: inlineOverlay } },
  })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  for (const fn of [list, update, remove, reorder]) fn.mockReset()
  first = rule({ name: 'Premium via header', priority: 0, scope: 'Team', scopeId: 't1', scopeName: 'Service desk', condition: 'headers["x-tier"] == "premium"' })
  second = rule({ name: 'Budget guard', priority: 10, scope: 'Team', scopeId: 't1', scopeName: 'Service desk', condition: 'budget_used > 90' })
  global = rule({ name: 'Everyone', scope: 'Global' })
  orphan = rule({ name: 'Left behind', scope: 'Department', scopeId: 'gone', scopeName: null, isOrphaned: true, isEnabled: false })
})

describe('RoutingRulesView', () => {
  it('shows the rules by scope in checking order and opens the first one', async () => {
    const wrapper = await mountView([global, second, first])
    expect(wrapper.find('h1').text()).toBe('Routing rules')
    expect(wrapper.findAll('nav h2').map((h) => h.text())).toEqual(['Team rules', 'Global rules'])
    expect(wrapper.find('#rule-title').text()).toBe('Premium via header')
    expect(wrapper.text()).toContain('Checked 1 of 2 for Service desk')
  })

  it('says what to do when there are no rules yet', async () => {
    const wrapper = await mountView([])
    expect(wrapper.text()).toContain('No routing rules yet. Without rules every request goes to the model it asked for.')
    expect(button(wrapper, 'New rule').exists()).toBe(true)
  })

  it('shows the error and lets the user try again', async () => {
    setActivePinia(createPinia())
    list.mockRejectedValueOnce(new ApiError(500, 'GET', null)).mockResolvedValueOnce([global])
    const wrapper = mount(RoutingRulesView, { global: { stubs: { RuleFormDrawer: true, ReassignRuleDialog: true, RuleTestDialog: true } } })
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('The server had a problem')
    await button(wrapper, 'Try again').trigger('click')
    await flushPromises()
    expect(wrapper.find('#rule-title').text()).toBe('Everyone')
  })

  it('filters by scope and keeps a valid rule selected', async () => {
    const wrapper = await mountView([first, second, global])
    expect(wrapper.findAll('[aria-pressed]').map((b) => b.text())).toEqual(['All3', 'Key0', 'Team2', 'Department0', 'Global1'])
    await wrapper.findAll('[aria-pressed]').find((b) => b.text().startsWith('Global'))!.trigger('click')
    expect(wrapper.findAll('nav li')).toHaveLength(1)
    expect(wrapper.find('#rule-title').text()).toBe('Everyone')
  })

  it('points out rules that need a new owner and can show only those', async () => {
    const wrapper = await mountView([first, orphan])
    expect(wrapper.text()).toContain('1 rule needs a new owner and is switched off.')
    await button(wrapper, 'Show').trigger('click')
    expect(wrapper.findAll('nav li')).toHaveLength(1)
    expect(wrapper.find('#rule-title').text()).toBe('Left behind')
    expect(wrapper.text()).toContain('This rule has no owner')
    expect(wrapper.text()).not.toContain('1 rule needs a new owner')
  })

  it('switches a rule off and confirms', async () => {
    const wrapper = await mountView([first, second])
    update.mockResolvedValue({ ...first, isEnabled: false })
    await button(wrapper, 'Disable').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledWith(first.id, expect.objectContaining({ isEnabled: false, name: first.name, condition: first.condition, priority: 0 }))
    expect(useUiStore().toasts.map((t) => t.message)).toContain('Rule disabled')
    expect(wrapper.find('nav').text()).toContain('Disabled')
  })

  it('moves a rule one place by sending the whole new order', async () => {
    const wrapper = await mountView([first, second])
    await wrapper.findAll('nav button').find((b) => b.text().includes('Budget guard'))!.trigger('click')
    reorder.mockResolvedValue([{ ...second, priority: 0 }, { ...first, priority: 10 }])
    await buttonByLabel(wrapper, 'Check earlier').trigger('click')
    await flushPromises()
    expect(reorder).toHaveBeenCalledWith({ scope: 'Team', scopeId: 't1', ruleIds: [second.id, first.id] })
    expect(wrapper.findAll('nav li').map((li) => li.text())[0]).toContain('Budget guard')
    expect(wrapper.find('[role="status"]').text()).toBe('Budget guard is now checked 1 of 2.')
    expect(wrapper.text()).toContain('Checked 1 of 2')
  })

  it('shows a failed action as an inline message and keeps the rule as it was', async () => {
    const wrapper = await mountView([first])
    update.mockRejectedValue(new ApiError(409, 'PUT', { detail: 'Ändringen krockar med befintliga uppgifter.' }))
    await button(wrapper, 'Disable').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('krockar')
    expect(wrapper.find('nav').text()).not.toContain('Disabled')
  })

  it('deletes a rule after confirmation', async () => {
    const wrapper = await mountView([first, second])
    await button(wrapper, 'Delete').trigger('click')
    expect(wrapper.text()).toContain('Delete rule?')
    remove.mockResolvedValue(undefined)
    await button(wrapper, 'Delete rule').trigger('click')
    await flushPromises()
    expect(remove).toHaveBeenCalledWith(first.id)
    expect(wrapper.find('#rule-title').text()).toBe('Budget guard')
    expect(useUiStore().toasts.map((t) => t.message)).toContain('Rule deleted')
  })
})
