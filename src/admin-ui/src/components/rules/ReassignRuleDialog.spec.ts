import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ApiError } from '@/api/client'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useUiStore } from '@/stores/ui'
import { inlineOverlay, rule } from '@/test/fixtures'
import { button } from '@/test/helpers'
import ReassignRuleDialog from './ReassignRuleDialog.vue'

const reassign = vi.fn()
vi.mock('@/api', () => ({ api: { routingRules: { reassign: (...a: unknown[]) => reassign(...a) } } }))
vi.mock('@/stores/teams', () => ({ useTeamsStore: () => ({ items: [{ id: 't2', name: 'Ny grupp', departmentName: 'KS' }], load: vi.fn() }) }))
vi.mock('@/stores/departments', () => ({ useDepartmentsStore: () => ({ items: [{ id: 'd2', name: 'Ny förvaltning' }], load: vi.fn() }) }))
vi.mock('@/stores/keys', () => ({ useKeysStore: () => ({ items: [], load: vi.fn() }) }))

const orphan = rule({ name: 'Premium', scope: 'Team', scopeId: 'gone', isOrphaned: true, isEnabled: false })

async function mountDialog(target = orphan) {
  const pinia = createPinia()
  setActivePinia(pinia)
  useRoutingRulesStore(pinia).items = [target]
  const wrapper = mount(ReassignRuleDialog, { props: { open: false, rule: target }, global: { plugins: [pinia], stubs: { UiDialog: inlineOverlay } } })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

describe('ReassignRuleDialog', () => {
  beforeEach(() => {
    reassign.mockReset()
  })

  it('asks for a new owner of the same kind the rule had, and enables the rule by default', async () => {
    const wrapper = await mountDialog()
    expect(wrapper.text()).toContain('Assign a new owner')
    expect(wrapper.text()).toContain('keeps its condition, targets and order')
    expect(wrapper.findAll('[role="radio"]').find((r) => r.attributes('aria-checked') === 'true')?.text()).toBe('Team')
    expect((wrapper.find('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(true)
  })

  it('needs an owner before it saves', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Choose the team this rule should apply to.')
    expect(reassign).not.toHaveBeenCalled()
  })

  it('assigns the new owner, enables the rule and confirms', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('#reassign-owner').setValue('t2')
    reassign.mockResolvedValue({ ...orphan, scopeId: 't2', scopeName: 'Ny grupp', isOrphaned: false, isEnabled: true })
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(reassign).toHaveBeenCalledWith(orphan.id, { scope: 'Team', scopeId: 't2', enable: true })
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(useUiStore().toasts.map((t) => t.message)).toContain('Rule assigned and enabled')
  })

  it('can assign without enabling, and can make the rule global', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.findAll('[role="radio"]').find((r) => r.text() === 'Global')!.trigger('click')
    expect(wrapper.find('#reassign-owner').exists()).toBe(false)
    reassign.mockResolvedValue({ ...orphan, scope: 'Global', scopeId: null, isOrphaned: false })
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(reassign).toHaveBeenCalledWith(orphan.id, { scope: 'Global', scopeId: null, enable: false })
  })

  it('shows a clash such as a duplicate name inside the dialog', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('#reassign-owner').setValue('t2')
    reassign.mockRejectedValue(new ApiError(409, 'POST', { detail: 'Det finns redan en regel med samma namn i detta omfång.' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('samma namn')
    expect(wrapper.emitted('update:open')).toBeUndefined()
  })

  it('for a rule that has an owner it is called "Change owner"', async () => {
    const wrapper = await mountDialog(rule({ scope: 'Team', scopeId: 't1', scopeName: 'Service desk' }))
    expect(wrapper.text()).toContain('Change owner')
    expect(button(wrapper, 'Assign').exists()).toBe(true)
  })
})
