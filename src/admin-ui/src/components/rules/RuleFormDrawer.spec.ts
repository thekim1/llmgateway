import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import { ApiError } from '@/api/client'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useUiStore } from '@/stores/ui'
import { inlineOverlay, rule } from '@/test/fixtures'
import { button, buttonByLabel } from '@/test/helpers'
import RuleFormDrawer from './RuleFormDrawer.vue'

const create = vi.fn()
const update = vi.fn()
const validate = vi.fn()
vi.mock('@/api', () => ({
  api: { routingRules: { create: (...a: unknown[]) => create(...a), update: (...a: unknown[]) => update(...a), validate: (...a: unknown[]) => validate(...a) } },
}))
vi.mock('@/composables/useModelOptions', () => ({
  useModelOptions: () => ({
    options: ref([
      { name: 'ume/chat-standard', source: 'route', residencies: [], providers: [] },
      { name: 'ume/chat-advanced', source: 'route', residencies: [], providers: [] },
    ]),
    loading: ref(false),
    error: ref(null),
    load: vi.fn(),
  }),
}))
vi.mock('@/stores/teams', () => ({ useTeamsStore: () => ({ items: [{ id: 't1', name: 'Service desk', departmentName: 'KS' }, { id: 't2', name: 'Ny grupp', departmentName: 'KS' }], load: vi.fn() }) }))
vi.mock('@/stores/departments', () => ({ useDepartmentsStore: () => ({ items: [{ id: 'd1', name: 'Kommunstyrelsen' }], load: vi.fn() }) }))
vi.mock('@/stores/keys', () => ({ useKeysStore: () => ({ items: [{ id: 'k1', name: 'CRM', prefix: 'ume-sk-ab' }], load: vi.fn() }) }))

const ok = { valid: true, errors: [], variables: [], available: [] }

function mountForm(existing = null as ReturnType<typeof rule> | null) {
  const pinia = createPinia()
  setActivePinia(pinia)
  return { pinia, wrapper: mount(RuleFormDrawer, { props: { open: true, rule: existing }, global: { plugins: [pinia], stubs: { UiDrawer: inlineOverlay } } }) }
}

async function fillBasics(wrapper: ReturnType<typeof mountForm>['wrapper'], name = 'Premium', model = 'ume/chat-advanced') {
  await wrapper.find('#rule-name').setValue(name)
  await wrapper.find('#rule-target-0').setValue(model)
}

describe('RuleFormDrawer', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
    validate.mockReset().mockResolvedValue(ok)
  })

  it('asks for the missing pieces without calling the server', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Give the rule a name.')
    expect(wrapper.text()).toContain('Choose a model or route for target 1.')
    expect(create).not.toHaveBeenCalled()
  })

  it('creates a global rule with trimmed values, placed first, enabled', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper, '  Premium  ', ' ume/chat-advanced ')
    create.mockResolvedValue(rule({ name: 'Premium' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith({
      name: 'Premium', description: null, scope: 'Global', scopeId: null, isEnabled: true, priority: 0, condition: '', chain: false,
      targets: [{ model: 'ume/chat-advanced', weight: 1 }], fallbacks: [],
    })
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(useUiStore().toasts.map((t) => t.message)).toContain('Rule saved')
  })

  it('needs an owner for a team rule and puts the new rule after that team’s existing ones', async () => {
    const { wrapper, pinia } = mountForm()
    useRoutingRulesStore(pinia).items = [rule({ scope: 'Team', scopeId: 't1', scopeName: 'Service desk', priority: 20 })]
    await flushPromises()
    await wrapper.findAll('[role="radio"]').find((r) => r.text() === 'Team')!.trigger('click')
    await fillBasics(wrapper)
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Choose the team this rule applies to.')
    expect(create).not.toHaveBeenCalled()

    await wrapper.find('#rule-owner').setValue('t1')
    create.mockResolvedValue(rule())
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ scope: 'Team', scopeId: 't1', priority: 30 }))
  })

  it('does not save while the condition is known to be invalid', async () => {
    validate.mockResolvedValue({ valid: false, errors: [{ position: 0, length: 3, message: 'Unknown variable.' }], variables: [], available: [] })
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper)
    await wrapper.find('textarea').setValue('bad')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Unknown variable.'))
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Fix the condition first.')
    expect(create).not.toHaveBeenCalled()
  })

  it('validates weights and shows each target’s share of the traffic', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper)
    await buttonByLabel(wrapper, 'Remove target 1').exists()
    await button(wrapper, 'Add target').trigger('click')
    await wrapper.find('#rule-target-1').setValue('ume/chat-standard')
    const weights = wrapper.findAll('input[type="number"]')
    await weights[0]!.setValue('70')
    await weights[1]!.setValue('30')
    expect(wrapper.text()).toContain('70% of the traffic')
    expect(wrapper.text()).toContain('30% of the traffic')
    await weights[1]!.setValue('0')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Weight for target 2 must be a whole number, 1 or higher.')
    expect(create).not.toHaveBeenCalled()
  })

  it('warns about a name that is not a model or route unless the rule is chained', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await wrapper.find('#rule-target-0').setValue('gpt-4-turbo')
    expect(wrapper.text()).toContain('Not an existing model or route.')
    await wrapper.findAll('label').find((l) => l.text().startsWith('Chain'))!.find('input').setValue(true)
    expect(wrapper.text()).not.toContain('Not an existing model or route.')
  })

  it('keeps the order of fallbacks and can move them', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper)
    await button(wrapper, 'Add fallback').trigger('click')
    await button(wrapper, 'Add fallback').trigger('click')
    await wrapper.find('#rule-fallback-0').setValue('ume/chat-standard')
    await wrapper.find('#rule-fallback-1').setValue('ume/chat-advanced')
    await buttonByLabel(wrapper, 'Move fallback 2 earlier').trigger('click')
    create.mockResolvedValue(rule())
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ fallbacks: ['ume/chat-advanced', 'ume/chat-standard'] }))
  })

  it('shows the server’s field errors where they belong', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper)
    create.mockRejectedValue(new ApiError(400, 'POST', { errors: { 'Targets[0].weight': ['Värdet är ogiltigt.'], condition: ['Okänd variabel (tecken 1)'] } }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Värdet är ogiltigt.')
    expect(wrapper.text()).toContain('Okänd variabel (tecken 1)')
    expect(wrapper.emitted('saved')).toBeUndefined()
  })

  it('shows a conflict such as a duplicate name as a form-level message', async () => {
    const { wrapper } = mountForm()
    await flushPromises()
    await fillBasics(wrapper)
    create.mockRejectedValue(new ApiError(409, 'POST', { detail: 'Det finns redan en regel med samma namn i detta omfång.' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('samma namn')
  })

  it('edits an existing rule and keeps its place in the order', async () => {
    const existing = rule({ name: 'Old', priority: 40, condition: 'budget_used > 90', targets: [{ model: 'ume/chat-advanced', weight: 3 }], fallbacks: ['ume/chat-standard'], chain: true })
    const { wrapper } = mountForm(existing)
    await flushPromises()
    expect((wrapper.find('#rule-name').element as HTMLInputElement).value).toBe('Old')
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe('budget_used > 90')
    update.mockResolvedValue({ ...existing, name: 'New' })
    await wrapper.find('#rule-name').setValue('New')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).toHaveBeenCalledWith(existing.id, expect.objectContaining({ name: 'New', priority: 40, chain: true, fallbacks: ['ume/chat-standard'], targets: [{ model: 'ume/chat-advanced', weight: 3 }] }))
  })

  it('cannot enable a rule that has no owner until a new owner is chosen', async () => {
    const orphan = rule({ scope: 'Team', scopeId: 'gone', scopeName: null, isOrphaned: true, isEnabled: false })
    const { wrapper } = mountForm(orphan)
    await flushPromises()
    const enabled = wrapper.findAll('label').find((l) => l.text().startsWith('Enabled'))!.find('input')
    expect(enabled.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('Choose a new owner above to enable it.')

    update.mockResolvedValue(orphan)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).toHaveBeenLastCalledWith(orphan.id, expect.objectContaining({ isEnabled: false, scopeId: 'gone' }))

    await wrapper.find('#rule-owner').setValue('t2')
    expect(enabled.attributes('disabled')).toBeUndefined()
    expect((enabled.element as HTMLInputElement).checked).toBe(true)
  })
})
