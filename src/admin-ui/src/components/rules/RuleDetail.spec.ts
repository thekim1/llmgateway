import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { rule } from '@/test/fixtures'
import { button, buttonByLabel } from '@/test/helpers'
import RuleDetail from './RuleDetail.vue'

const mountDetail = (r: ReturnType<typeof rule>, props: { position?: number; count?: number; busy?: boolean } = {}) =>
  mount(RuleDetail, { props: { rule: r, position: 1, count: 1, ...props } })

describe('RuleDetail', () => {
  it('shows the condition, the targets with their share of the traffic and the fallbacks', () => {
    const wrapper = mountDetail(
      rule({ name: 'Premium', condition: 'headers["x-tier"] == "premium"', targets: [{ model: 'a', weight: 70 }, { model: 'b', weight: 30 }], fallbacks: ['c'], description: 'For paying users' }),
    )
    const text = wrapper.text()
    expect(text).toContain('Premium')
    expect(text).toContain('For paying users')
    expect(wrapper.find('code').text()).toBe('headers["x-tier"] == "premium"')
    expect(text).toContain('70% · weight 70')
    expect(text).toContain('30% · weight 30')
    expect(text).toContain('Then, if those fail')
    expect(wrapper.find('ol li').text()).toContain('c')
  })

  it('explains an empty condition and a chain', () => {
    const wrapper = mountDetail(rule({ condition: '  ', chain: true, scope: 'Team', scopeId: 't', scopeName: 'Service desk' }))
    expect(wrapper.text()).toContain('No condition: it matches every request from Service desk.')
    expect(wrapper.text()).toContain('Chain: after this rule')
  })

  it('tells the user a rule without an owner is switched off and offers a new owner', async () => {
    const wrapper = mountDetail(rule({ scope: 'Team', scopeId: 'gone', isOrphaned: true, isEnabled: false }))
    expect(wrapper.text()).toContain('This rule has no owner')
    expect(wrapper.text()).toContain('is switched off and never applies')
    const enable = button(wrapper, 'Enable')
    expect(enable.attributes('aria-disabled')).toBe('true')
    expect(wrapper.text()).toContain('Can’t enable a rule without an owner')
    await button(wrapper, 'Assign a new owner').trigger('click')
    expect(wrapper.emitted('reassign')).toHaveLength(1)
  })

  it('lists why the gateway ignores an enabled but invalid rule', () => {
    const wrapper = mountDetail(rule({ validationErrors: [{ message: 'Unknown variable', position: 3, length: 2 }] }))
    expect(wrapper.text()).toContain('The gateway ignores this rule until it is fixed')
    expect(wrapper.text()).toContain('Unknown variable')
  })

  it('emits the actions and moves one place at a time', async () => {
    const wrapper = mountDetail(rule({ scope: 'Team', scopeId: 't', scopeName: 'Service desk' }), { position: 2, count: 3 })
    expect(wrapper.text()).toContain('Checked 2 of 3 for Service desk')
    await button(wrapper, 'Edit').trigger('click')
    await button(wrapper, 'Disable').trigger('click')
    await button(wrapper, 'Change owner').trigger('click')
    await button(wrapper, 'Delete').trigger('click')
    await buttonByLabel(wrapper, 'Check earlier').trigger('click')
    await buttonByLabel(wrapper, 'Check later').trigger('click')
    expect(['edit', 'toggle', 'reassign', 'delete'].map((e) => wrapper.emitted(e)?.length)).toEqual([1, 1, 1, 1])
    expect(wrapper.emitted('move')).toEqual([[-1], [1]])
  })

  it('cannot move past either end, and hides moving when there is only one rule', () => {
    const first = mountDetail(rule(), { position: 1, count: 2 })
    expect(buttonByLabel(first, 'Check earlier').attributes('aria-disabled')).toBe('true')
    expect(buttonByLabel(first, 'Check later').attributes('aria-disabled')).toBeUndefined()
    const last = mountDetail(rule(), { position: 2, count: 2 })
    expect(buttonByLabel(last, 'Check later').attributes('aria-disabled')).toBe('true')
    const only = mountDetail(rule(), { position: 1, count: 1 })
    expect(only.find('button[aria-label="Check earlier"]').exists()).toBe(false)
  })

  it('offers to change the scope of a global rule', () => {
    expect(button(mountDetail(rule()), 'Change scope').exists()).toBe(true)
  })
})
