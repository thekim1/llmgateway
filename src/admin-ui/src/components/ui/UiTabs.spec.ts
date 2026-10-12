import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import UiTabs from './UiTabs.vue'

const tabs = [
  { value: 'a', label: 'First' },
  { value: 'b', label: 'Second' },
  { value: 'c', label: 'Third' },
]

describe('UiTabs', () => {
  it('marks the selected tab, ties it to its panel and keeps only it in the tab order', () => {
    const wrapper = mount(UiTabs, { props: { id: 'x', tabs, modelValue: 'b', label: 'Views' } })
    const [first, second] = wrapper.findAll('[role="tab"]')
    expect(wrapper.find('[role="tablist"]').attributes('aria-label')).toBe('Views')
    expect(second!.attributes()).toMatchObject({ 'aria-selected': 'true', 'aria-controls': 'x-panel-b', id: 'x-tab-b', tabindex: '0' })
    expect(first!.attributes()).toMatchObject({ 'aria-selected': 'false', tabindex: '-1' })
  })

  it('selects with arrow keys, Home and End, wrapping around', async () => {
    const wrapper = mount(UiTabs, { props: { id: 'x', tabs, modelValue: 'a', label: 'Views' } })
    const all = wrapper.findAll('[role="tab"]')
    await all[0]!.trigger('keydown', { key: 'ArrowLeft' })
    await all[0]!.trigger('keydown', { key: 'ArrowRight' })
    await all[1]!.trigger('keydown', { key: 'Home' })
    await all[0]!.trigger('keydown', { key: 'End' })
    expect(wrapper.emitted('update:modelValue')).toEqual([['c'], ['b'], ['a'], ['c']])
  })
})
