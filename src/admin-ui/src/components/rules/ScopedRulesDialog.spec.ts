import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import { ApiError } from '@/api/client'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import ScopedRulesDialog from './ScopedRulesDialog.vue'

const rules = [
  { id: 'r1', name: 'Premium via header', isEnabled: true },
  { id: 'r2', name: 'Old experiment', isEnabled: false },
]

function mountDialog(action = vi.fn().mockResolvedValue(undefined), kind: 'team' | 'department' = 'team') {
  return { action, wrapper: mount(ScopedRulesDialog, { props: { open: true, kind, name: 'Servicedesk', rules, action }, global: { stubs: { UiDialog: inlineOverlay } } }) }
}

describe('ScopedRulesDialog', () => {
  it('lists the rules and recommends keeping them switched off', () => {
    const { wrapper } = mountDialog()
    const text = wrapper.text()
    expect(text).toContain('2 routing rules belong to this team')
    expect(text).toContain('Premium via header')
    expect(text).toContain('Old experiment · already disabled')
    expect(text).toContain('Deactivate them (recommended)')
    expect((wrapper.find('input[value="deactivate"]').element as HTMLInputElement).checked).toBe(true)
    expect(button(wrapper, 'Delete team, deactivate 2 rules').exists()).toBe(true)
  })

  it('deactivates by default', async () => {
    const { wrapper, action } = mountDialog()
    await button(wrapper, 'Delete team, deactivate 2 rules').trigger('click')
    await flushPromises()
    expect(action).toHaveBeenCalledWith('deactivate')
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
  })

  it('deletes the rules only when asked, and says so on the button', async () => {
    const { wrapper, action } = mountDialog()
    await wrapper.find('input[value="delete"]').setValue(true)
    const confirm = button(wrapper, 'Delete team and 2 rules')
    await confirm.trigger('click')
    await flushPromises()
    expect(action).toHaveBeenCalledWith('delete')
  })

  it('talks about departments when a department is deleted', () => {
    const { wrapper } = mountDialog(undefined, 'department')
    expect(wrapper.text()).toContain('belong to this department')
    expect(button(wrapper, 'Delete department, deactivate 2 rules').exists()).toBe(true)
  })

  it('keeps the dialog open and shows the server’s message when the delete fails', async () => {
    const action = vi.fn().mockRejectedValue(new ApiError(409, 'DELETE', { detail: 'Teamet har nycklar och kan inte tas bort.' }))
    const { wrapper } = mountDialog(action)
    await button(wrapper, 'Delete team, deactivate').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Teamet har nycklar')
    expect(wrapper.emitted('update:open')).toBeUndefined()
  })

  it('starts from the safe choice every time it opens', async () => {
    const { wrapper } = mountDialog()
    await wrapper.find('input[value="delete"]').setValue(true)
    await wrapper.setProps({ open: false })
    await wrapper.setProps({ open: true })
    expect((wrapper.find('input[value="deactivate"]').element as HTMLInputElement).checked).toBe(true)
  })

  it('can be cancelled', async () => {
    const { wrapper, action } = mountDialog()
    await button(wrapper, 'Cancel').trigger('click')
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(action).not.toHaveBeenCalled()
  })
})
