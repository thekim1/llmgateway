import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import { ApiError } from '@/api/client'
import type { RoutingRuleTestResult } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import RuleTestDialog from './RuleTestDialog.vue'

const test = vi.fn()
vi.mock('@/api', () => ({ api: { routingRules: { test: (...a: unknown[]) => test(...a) } } }))
vi.mock('@/composables/useModelOptions', () => ({ useModelOptions: () => ({ options: ref([{ name: 'ume/chat-standard', source: 'route', residencies: [], providers: [] }]), loading: ref(false), error: ref(null), load: vi.fn() }) }))
vi.mock('@/stores/keys', () => ({ useKeysStore: () => ({ items: [{ id: 'k1', name: 'CRM', prefix: 'ume-sk-ab' }], load: vi.fn() }) }))
vi.mock('@/stores/teams', () => ({ useTeamsStore: () => ({ items: [{ id: 't1', name: 'Service desk', departmentName: 'KS' }], load: vi.fn() }) }))

const matched: RoutingRuleTestResult = {
  matched: true,
  primaryModel: 'ume/chat-advanced',
  models: [
    { name: 'ume/chat-advanced', exists: true, type: 'alias', kind: 'Chat', enabled: true },
    { name: 'gone/model', exists: false, type: 'unknown', kind: null, enabled: false },
  ],
  applied: [
    { ruleId: 'r1', name: 'Normalise', fromModel: 'gpt-4', toModel: 'ume/chat-standard' },
    { ruleId: 'r2', name: 'Premium via header', fromModel: 'ume/chat-standard', toModel: 'ume/chat-advanced' },
  ],
  chainLimitReached: false,
  evaluation: [
    { ruleId: 'r1', name: 'Normalise', scope: 'Global', priority: 0, chainStep: 0, model: 'gpt-4', outcome: 'Matched', trace: [{ text: 'model == "gpt-4"', leftValue: '"gpt-4"', result: true }] },
    {
      ruleId: 'r2', name: 'Premium via header', scope: 'Team', priority: 0, chainStep: 1, model: 'ume/chat-standard', outcome: 'Matched',
      trace: [
        { text: 'headers["x-tier"] == "premium"', leftValue: '"premium"', result: true },
        { text: 'budget_used > 90', leftValue: null, result: null },
        { text: 'model == "other"', leftValue: '"ume/chat-standard"', result: false },
      ],
    },
    { ruleId: 'r1', name: 'Normalise', scope: 'Global', priority: 0, chainStep: 1, model: 'ume/chat-standard', outcome: 'Skipped', trace: [] },
  ],
  ignoredRules: [{ ruleId: 'r9', ruleName: 'Broken rule', message: 'Unknown variable', position: 2, length: 3 }],
  note: 'Only the usage values you supply are known to the rules.',
}

async function mountDialog() {
  setActivePinia(createPinia())
  const wrapper = mount(RuleTestDialog, { props: { open: false }, global: { stubs: { UiDialog: inlineOverlay } } })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

describe('RuleTestDialog', () => {
  beforeEach(() => {
    test.mockReset()
  })

  it('needs a model name before it runs', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Enter the model name the client would ask for.')
    expect(test).not.toHaveBeenCalled()
  })

  it('sends only what was filled in, keeping numbers and booleans typed', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValue({ ...matched, applied: [], matched: false, models: [], evaluation: [], ignoredRules: [] })
    await wrapper.find('#test-model').setValue(' gpt-4 ')
    await wrapper.find('#test-key').setValue('k1')
    await button(wrapper, 'Add header').trigger('click')
    await button(wrapper, 'Add parameter').trigger('click')
    await button(wrapper, 'Add parameter').trigger('click')
    const inputs = wrapper.findAll('details input.field-input')
    await inputs[0]!.setValue('x-ume-tier')
    await inputs[1]!.setValue('premium')
    await inputs[2]!.setValue('max_tokens')
    await inputs[3]!.setValue('2000')
    await inputs[4]!.setValue('stream')
    await inputs[5]!.setValue('true')
    await wrapper.find('#test-budget').setValue('92,5')
    await wrapper.find('#test-prompt').setValue('1200')
    await wrapper.find('#test-pii').setValue('yes')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(test).toHaveBeenCalledWith({
      model: 'gpt-4',
      endpoint: 'chat_completions',
      headers: { 'x-ume-tier': 'premium' },
      params: { max_tokens: 2000, stream: true },
      keyId: 'k1',
      budgetUsed: 92.5,
      promptTokens: 1200,
      piiDetected: true,
    })
  })

  it('rejects an impossible usage value without calling the server', async () => {
    const wrapper = await mountDialog()
    await wrapper.find('#test-model').setValue('x')
    await wrapper.find('#test-budget').setValue('150')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Budget used must be a number from 0 to 100.')
    expect(test).not.toHaveBeenCalled()
  })

  it('explains which rule applies, where the request goes and why each rule matched or not', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValue(matched)
    await wrapper.find('#test-model').setValue('gpt-4')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('Premium via header applies')
    expect(text).toContain('1. Normalise: gpt-4 → ume/chat-standard')
    expect(text).toContain('2. Premium via header: ume/chat-standard → ume/chat-advanced')
    expect(text).toContain('Does not exist')
    expect(text).toContain('Route')
    expect(text).toContain('Skipped: already applied')
    expect(text).toContain('The gateway ignores these rules because they are invalid')
    expect(text).toContain('Broken rule')
    expect(text).toContain('Only the usage values you supply are known to the rules.')
  })

  it('shows true, false and "could not be evaluated" as words, not only as colour', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValue(matched)
    await wrapper.find('#test-model').setValue('gpt-4')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const items = wrapper.findAll('section[aria-label="Test result"] ul li').map((li) => li.text())
    expect(items.find((t) => t.includes('x-tier'))).toContain('true')
    expect(items.find((t) => t.includes('budget_used'))).toContain('could not be evaluated (a value is missing)')
    expect(items.find((t) => t.includes('"other"'))).toContain('false')
    expect(items.find((t) => t.includes('"other"'))).toContain('was "ume/chat-standard"')
  })

  it('says plainly when no rule matches', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValue({ ...matched, matched: false, primaryModel: null, models: [], applied: [], evaluation: [], ignoredRules: [] })
    await wrapper.find('#test-model').setValue('ume/chat-standard')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('No rule matches')
    expect(wrapper.text()).toContain('The request goes to the model it asked for, ume/chat-standard.')
    expect(wrapper.text()).toContain('No enabled rule applies to this key, team or department.')
  })

  it('warns when a chain was cut off', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValue({ ...matched, chainLimitReached: true })
    await wrapper.find('#test-model').setValue('gpt-4')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('The chain stopped after the maximum number of steps.')
  })

  it('shows the server’s message when the test fails and clears an old result', async () => {
    const wrapper = await mountDialog()
    test.mockResolvedValueOnce(matched)
    await wrapper.find('#test-model').setValue('gpt-4')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('applies')
    test.mockRejectedValueOnce(new ApiError(400, 'POST', { detail: 'Kontrollera de markerade fälten.' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Kontrollera')
    expect(wrapper.find('section[aria-label="Test result"]').exists()).toBe(false)
  })
})
