import { describe, expect, it } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import axe from 'axe-core'
import { createAppI18n } from '@/i18n'
import { THEME_IDS, applyTheme } from '@/theme/themes'
import { routes } from '@/router'
import sv from '@/i18n/locales/sv'
import en from '@/i18n/locales/en'
import ShowSecretDialog from './ShowSecretDialog.vue'
import BudgetMeter from './BudgetMeter.vue'
import ThemePicker from './ThemePicker.vue'
import FormField from './form/FormField.vue'

const plugins = () => [createPinia(), createAppI18n()]
describe('accessible components and complete navigation', () => {
  it('does not let a one-time secret disappear without acknowledgement', async () => {
    const wrapper = mount(ShowSecretDialog, { props: { open: true, secret: 'temporary-secret', keyName: 'Test' }, attachTo: document.body, global: { plugins: plugins() } })
    await flushPromises()
    document.querySelector<HTMLButtonElement>('[data-testid="secret-close"]')!.click()
    await flushPromises()
    expect(wrapper.emitted('closed')).toBeUndefined()
    const checkbox = document.querySelector<HTMLInputElement>('#secret-ack')!
    checkbox.checked = true
    checkbox.dispatchEvent(new Event('change', { bubbles: true }))
    await flushPromises()
    document.querySelector<HTMLButtonElement>('[data-testid="secret-close"]')!.click()
    await flushPromises()
    expect(wrapper.emitted('closed')).toHaveLength(1)
    wrapper.unmount()
  })
  it.each(THEME_IDS)('passes component axe checks in %s', async theme => {
    applyTheme(theme)
    const wrapper = mount(FormField, { props: { id: 'test-name', label: 'Namn', help: 'Ange ett namn.', required: true }, slots: { default: '<input id="test-name" aria-describedby="test-name-help">' }, attachTo: document.body, global: { plugins: plugins() } })
    const result = await axe.run(wrapper.element as HTMLElement, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
    wrapper.unmount()
  })
  it('uses translated labels for every theme preference', () => {
    const wrapper = mount(ThemePicker, { global: { plugins: plugins() } })
    expect(wrapper.text()).not.toContain('theme.options.')
    expect(wrapper.findAll('option')).toHaveLength(5)
  })
  it('provides a text alternative to the budget chart', () => {
    const wrapper = mount(BudgetMeter, { props: { label: 'Team', limit: 100, spent: 80 }, global: { plugins: plugins() } })
    expect(wrapper.get('[role="meter"]').attributes('aria-valuetext')).toContain('80')
    expect(wrapper.text()).toContain('Varning')
  })
  it('represents a zero budget as blocked, not available', () => {
    const wrapper = mount(BudgetMeter, { props: { label: 'Team', limit: 0, spent: 0 }, global: { plugins: plugins() } })
    expect(wrapper.get('[role="meter"]').attributes('aria-valuenow')).toBe('100')
    expect(wrapper.text()).toContain('Budgetgränsen')
  })
  it('loads every routed page, not only declared module types', async () => {
    for (const route of routes) {
      if (typeof route.component === 'function' && !('props' in route.component)) {
        const load = route.component as () => Promise<unknown>
        expect(await load()).toBeTruthy()
      }
    }
  })
  it('has the same translation keys in both languages', () => {
    function keys(value: object, prefix = ''): string[] {
      return Object.entries(value).flatMap(([key, item]) => typeof item === 'string' ? [prefix + key] : keys(item, prefix + key + '.'))
    }
    expect(keys(sv).sort()).toEqual(keys(en).sort())
  })
})
