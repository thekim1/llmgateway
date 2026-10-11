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
const setEnabled = vi.fn()
vi.mock('@/api', () => ({
  api: {
    routingRules: {
      list: (...a: unknown[]) => list(...a),
      update: (...a: unknown[]) => update(...a),
      remove: (...a: unknown[]) => remove(...a),
      reorder: (...a: unknown[]) => reorder(...a),
      setEnabled: (...a: unknown[]) => setEnabled(...a),
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
  for (const fn of [list, update, remove, reorder, setEnabled]) fn.mockReset()
  first = rule({ name: 'Premium via header', priority: 0, scope: 'Team', scopeIds: ['t1'], owners: [{ id: 't1', name: 'Service desk' }], condition: 'headers["x-tier"] == "premium"' })
  second = rule({ name: 'Budget guard', priority: 10, scope: 'Team', scopeIds: ['t1'], owners: [{ id: 't1', name: 'Service desk' }], condition: 'budget_used > 90' })
  global = rule({ name: 'Everyone', scope: 'Global' })
  orphan = rule({ name: 'Left behind', scope: 'Department', scopeIds: [], isOrphaned: true, isEnabled: false })
})

describe('RoutingRulesView', () => {
  it('shows the rules by scope in checking order and opens the first one', async () => {
    const wrapper = await mountView([global, second, first])
    expect(wrapper.find('h1').text()).toBe('Routing rules')
    expect(wrapper.findAll('nav h2').map((h) => h.text())).toEqual(['Team rules', 'Global rules'])
    expect(wrapper.find('#rule-title').text()).toBe('Premium via header')
    expect(wrapper.text()).toContain('Number 1 of 2 among the team rules')
    expect(wrapper.find('nav').text()).toContain('Service desk')
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
    setEnabled.mockResolvedValue({ ...first, isEnabled: false })
    await button(wrapper, 'Disable').trigger('click')
    await flushPromises()
    expect(setEnabled).toHaveBeenCalledWith(first.id, false)
    expect(update).not.toHaveBeenCalled()
    expect(useUiStore().toasts.map((t) => t.message)).toContain('Premium via header disabled')
    expect(wrapper.find('nav').text()).toContain('Off')
  })

  it('has an accessible on/off switch for every rule in the list', async () => {
    const wrapper = await mountView([first, second, orphan])
    const switches = wrapper.findAll('nav [role="switch"]')
    expect(switches.map((s) => [s.attributes('aria-label'), s.attributes('aria-checked')])).toEqual([
      ['Enable Premium via header', 'true'],
      ['Enable Budget guard', 'true'],
      ['Enable Left behind', 'false'],
    ])
    expect(switches.every((s) => s.element.tagName === 'BUTTON' && s.attributes('type') === 'button')).toBe(true)
    setEnabled.mockResolvedValue({ ...second, isEnabled: false })
    await switches[1]!.trigger('click')
    await flushPromises()
    expect(setEnabled).toHaveBeenCalledWith(second.id, false)
    expect(wrapper.findAll('nav [role="switch"]')[1]!.attributes('aria-checked')).toBe('false')
  })

  it('leaves the switch off and says why when the server refuses to enable a rule', async () => {
    const template = rule({ name: 'Personal data to a local model', isTemplate: true, isEnabled: false, missingModels: ['local'] })
    const wrapper = await mountView([template])
    setEnabled.mockRejectedValue(new ApiError(400, 'PUT', { detail: 'Regeln kan inte aktiveras. Okänd modell eller alias: local.' }))
    await wrapper.find('nav [role="switch"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Could not switch “Personal data to a local model”')
    expect(wrapper.find('[role="alert"]').text()).toContain('local')
    expect(wrapper.find('nav [role="switch"]').attributes('aria-checked')).toBe('false')
    expect(wrapper.text()).toContain('Template')
  })

  it('moves a rule one place by sending the whole new order', async () => {
    const wrapper = await mountView([first, second])
    await wrapper.findAll('nav button').find((b) => b.text().includes('Budget guard'))!.trigger('click')
    reorder.mockResolvedValue([{ ...second, priority: 0 }, { ...first, priority: 10 }])
    await buttonByLabel(wrapper, 'Check earlier').trigger('click')
    await flushPromises()
    expect(reorder).toHaveBeenCalledWith({ scope: 'Team', ruleIds: [second.id, first.id] })
    expect(wrapper.findAll('nav li').map((li) => li.text())[0]).toContain('Budget guard')
    expect(wrapper.find('[role="status"]').text()).toBe('Budget guard is now checked 1 of 2.')
    expect(wrapper.text()).toContain('Number 1 of 2')
  })

  it('shows a failed action as an inline message and keeps the rule as it was', async () => {
    const wrapper = await mountView([first])
    setEnabled.mockRejectedValue(new ApiError(409, 'PUT', { detail: 'Ändringen krockar med befintliga uppgifter.' }))
    await button(wrapper, 'Disable').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('krockar')
    expect(wrapper.find('nav').text()).not.toContain('Off')
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
