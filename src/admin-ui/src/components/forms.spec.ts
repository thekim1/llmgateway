import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { createAppI18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { http } from '@/api/client'
import { api } from '@/api'
import ResourcePanel from './ResourcePanel.vue'
import RotateKeyDialog from './RotateKeyDialog.vue'

async function panel(resource: 'departments' | 'routes' | 'budgets', initial?: Record<string, string>) {
  const pinia = createPinia()
  const auth = useAuthStore(pinia)
  vi.spyOn(api.bff, 'user').mockResolvedValue({ isAuthenticated: true, name: 'Test', email: null, roles: ['gateway-admin'], departmentCodes: [], sessionExpiresAt: null })
  await auth.load()
  vi.spyOn(http, 'get').mockResolvedValue([])
  vi.spyOn(api.keys, 'list').mockResolvedValue([])
  const wrapper = mount(ResourcePanel, { props: { resource, title: 'Test', createOnly: true, initial }, attachTo: document.body, global: { plugins: [pinia, createAppI18n()] } })
  await flushPromises()
  return wrapper
}
describe('review and financial controls', () => {
  it('focuses validation errors rather than sending an invalid department', async () => {
    const post = vi.spyOn(http, 'post')
    const wrapper = await panel('departments')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('#departments-form-name').attributes('aria-invalid')).toBe('true')
    expect(document.activeElement?.id).toBe('departments-form-errors')
    expect(post).not.toHaveBeenCalled()
  })
  it('does not create a department until the review has been acknowledged', async () => {
    const post = vi.spyOn(http, 'post').mockResolvedValue({ id: 'department', name: 'Test' })
    const wrapper = await panel('departments')
    await wrapper.get('#departments-form-name').setValue('Test')
    await wrapper.get('#departments-form-costCenterCode').setValue('101')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const button = document.querySelector<HTMLButtonElement>('[data-testid="confirm-action"]')!
    button.click()
    await flushPromises()
    expect(post).not.toHaveBeenCalled()
    const acknowledgement = document.querySelector<HTMLInputElement>('[role="alertdialog"] input[type="checkbox"]')!
    acknowledgement.checked = true
    acknowledgement.dispatchEvent(new Event('change', { bubbles: true }))
    await flushPromises()
    button.click()
    await flushPromises()
    expect(post).toHaveBeenCalledWith('/api/departments', { name: 'Test', costCenterCode: '101', isActive: true })
  })
  it('requires at least one route target', async () => {
    const wrapper = await panel('routes')
    await wrapper.get('#routes-form-name').setValue('ume/test')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('#routes-form-errors').text()).not.toBe('')
    expect(document.querySelector('[role="alertdialog"]')).toBeNull()
  })
  it('preselects the newly created team for its wizard budget', async () => {
    const wrapper = await panel('budgets', { scope: 'Team', scopeId: 'new-team' })
    expect((wrapper.get('#budgets-form-scope').element as HTMLSelectElement).value).toBe('Team')
  })
  it('defaults rotation to immediate revocation', async () => {
    mount(RotateKeyDialog, { props: { open: true, keyName: 'Test' }, attachTo: document.body, global: { plugins: [createPinia(), createAppI18n()] } })
    await flushPromises()
    const checked = document.querySelector<HTMLInputElement>('[role="dialog"] input[type="radio"]:checked')
    expect(checked?.value).toBe('RevokeImmediately')
  })
})
