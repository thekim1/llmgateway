import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { rule } from '@/test/fixtures'
import { groupRules } from '@/utils/routingRules'
import RuleList from './RuleList.vue'

function mountList(rules: ReturnType<typeof rule>[], selectedId: string | null = null) {
  return mount(RuleList, { props: { sections: groupRules(rules), selectedId } })
}

describe('RuleList', () => {
  it('lists rules under their scope in checking order, with order numbers', () => {
    const a = rule({ name: 'Second', priority: 20, scope: 'Team', scopeIds: ['t1'], owners: [{ id: 't1', name: 'Service desk' }] })
    const b = rule({ name: 'First', priority: 10, scope: 'Team', scopeIds: ['t1'], owners: [{ id: 't1', name: 'Service desk' }] })
    const g = rule({ name: 'Everyone', scope: 'Global' })
    const wrapper = mountList([a, g, b])
    const headings = wrapper.findAll('h2').map((h) => h.text())
    expect(headings).toEqual(['Team rules', 'Global rules'])
    const items = wrapper.findAll('li').map((li) => li.text())
    expect(items[0]).toContain('1')
    expect(items[0]).toContain('First')
    expect(items[1]).toContain('2')
    expect(items[1]).toContain('Second')
    expect(wrapper.text()).toContain('Service desk')
  })

  it('says what each rule does in plain text, including anything unusual', () => {
    const wrapper = mountList([
      rule({ name: 'Split', targets: [{ model: 'a', weight: 1 }, { model: 'b', weight: 1 }], chain: true }),
      rule({ name: 'Off', isEnabled: false }),
      rule({ name: 'Lost', scope: 'Team', scopeIds: [], isOrphaned: true, isEnabled: false }),
      rule({ name: 'Broken', validationErrors: [{ message: 'bad', position: 0, length: 1 }] }),
    ])
    const text = wrapper.text()
    expect(text).toContain('→ a +1 · Chain')
    expect(text).toContain('Off')
    expect(text).toContain('Needs an owner')
    expect(text).toContain('Ignored: invalid')
    expect(wrapper.findAll('[role="img"]').map((i) => i.attributes('aria-label'))).toEqual(['Has no owner', 'Is invalid'])
  })

  it('marks the selected rule and emits the id when one is chosen', async () => {
    const a = rule({ name: 'A' })
    const b = rule({ name: 'B' })
    const wrapper = mountList([a, b], b.id)
    const buttons = wrapper.findAll('button:not([role="switch"])')
    expect(buttons.map((x) => x.attributes('aria-current'))).toEqual([undefined, 'true'])
    await buttons[0]!.trigger('click')
    expect(wrapper.emitted('select')).toEqual([[a.id]])
  })

  it('shows who a rule applies to, its description and a template badge', () => {
    const wrapper = mountList([
      rule({ name: 'Lost', scope: 'Department', scopeIds: [], isOrphaned: true }),
      rule({
        name: 'Shared',
        description: 'For the contact centre keys.',
        scope: 'VirtualKey',
        scopeIds: ['a', 'b', 'c'],
        owners: [{ id: 'a', name: 'CRM' }, { id: 'b', name: 'Chat' }, { id: 'c', name: 'Mail' }],
      }),
      rule({ name: 'Example', isTemplate: true, isEnabled: false }),
    ])
    const text = wrapper.text()
    expect(text).toContain('No owner')
    expect(text).toContain('For the contact centre keys.')
    expect(text).toContain('CRM, Chat +1')
    expect(text).toContain('Template · Off')
  })

  it('emits the wanted state from the switch without changing it itself', async () => {
    const a = rule({ name: 'A', isEnabled: false })
    const wrapper = mountList([a])
    const toggle = wrapper.find('[role="switch"]')
    expect(toggle.attributes('aria-checked')).toBe('false')
    expect(toggle.attributes('aria-label')).toBe('Enable A')
    await toggle.trigger('click')
    expect(wrapper.emitted('toggle')).toEqual([[a, true]])
    expect(toggle.attributes('aria-checked')).toBe('false')
    await wrapper.setProps({ pendingId: a.id })
    expect(toggle.attributes('aria-busy')).toBe('true')
    await toggle.trigger('click')
    expect(wrapper.emitted('toggle')).toHaveLength(1)
  })
})
