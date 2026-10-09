import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { ConditionCheck } from '@/api/types'
import ConditionEditor from './ConditionEditor.vue'

const validate = vi.fn()
vi.mock('@/api', () => ({ api: { routingRules: { validate: (...a: unknown[]) => validate(...a) } } }))

const available = [
  { name: 'budget_used', type: 'Number' },
  { name: 'headers', type: 'TextMap' },
]
const valid = (variables: string[] = []): ConditionCheck => ({ valid: true, errors: [], variables, available })

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => (resolve = r))
  return { promise, resolve }
}

beforeEach(() => {
  validate.mockReset()
  vi.useFakeTimers()
})
afterEach(() => vi.useRealTimers())

describe('ConditionEditor', () => {
  it('checks the condition once on open, which also teaches it the variable names', async () => {
    validate.mockResolvedValue(valid())
    const wrapper = mount(ConditionEditor, { props: { modelValue: '' } })
    await flushPromises()
    expect(validate).toHaveBeenCalledTimes(1)
    expect(validate).toHaveBeenCalledWith('')
    expect(wrapper.findAll('button[aria-label^="Insert "]').map((b) => b.text())).toEqual(['budget_used', 'headers'])
    expect(wrapper.text()).toContain('No condition: the rule matches every request in its scope.')
  })

  it('waits until typing pauses before asking the server', async () => {
    validate.mockResolvedValue(valid(['budget_used']))
    const wrapper = mount(ConditionEditor, { props: { modelValue: '' } })
    await flushPromises()
    validate.mockClear()

    await wrapper.setProps({ modelValue: 'budget' })
    await vi.advanceTimersByTimeAsync(300)
    await wrapper.setProps({ modelValue: 'budget_used > 9' })
    await vi.advanceTimersByTimeAsync(399)
    expect(validate).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(validate).toHaveBeenCalledTimes(1)
    expect(validate).toHaveBeenCalledWith('budget_used > 9')
    await flushPromises()
    expect(wrapper.text()).toContain('Valid')
    expect(wrapper.text()).toContain('uses budget_used')
  })

  it('explains an invalid condition with the position and the offending text', async () => {
    const source = 'budget_used > 5 && budget_usd > 1'
    validate.mockResolvedValue({ valid: false, errors: [{ position: 19, length: 10, message: "Unknown variable 'budget_usd'." }], variables: [], available })
    const wrapper = mount(ConditionEditor, { props: { modelValue: source } })
    await flushPromises()
    expect(wrapper.text()).toContain("Unknown variable 'budget_usd'. (character 20)")
    expect(wrapper.find('mark').text()).toBe('budget_usd')
    expect(wrapper.find('textarea').attributes('aria-invalid')).toBe('true')
    expect(wrapper.find('[role="status"]').exists()).toBe(true)
    expect(wrapper.emitted('status')?.at(-1)).toEqual(['invalid'])
  })

  it('reports valid, so the form can be saved', async () => {
    validate.mockResolvedValue(valid(['headers']))
    const wrapper = mount(ConditionEditor, { props: { modelValue: 'headers["x"] == "y"' } })
    await flushPromises()
    expect(wrapper.emitted('status')?.at(-1)).toEqual(['valid'])
    expect(wrapper.find('textarea').attributes('aria-invalid')).toBeUndefined()
  })

  it('never lets a slow answer for old text overwrite the answer for the current text', async () => {
    validate.mockResolvedValue(valid())
    const wrapper = mount(ConditionEditor, { props: { modelValue: '' } })
    await flushPromises()
    const slow = deferred<ConditionCheck>()
    const fast = deferred<ConditionCheck>()
    validate.mockReset().mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise)

    await wrapper.setProps({ modelValue: 'old' })
    await vi.advanceTimersByTimeAsync(400) // asks about "old" (slow)
    await wrapper.setProps({ modelValue: 'new' })
    await vi.advanceTimersByTimeAsync(400) // asks about "new" (fast)
    fast.resolve({ valid: true, errors: [], variables: ['headers'], available })
    await flushPromises()
    slow.resolve({ valid: false, errors: [{ position: 0, length: 3, message: 'stale problem' }], variables: [], available })
    await flushPromises()
    expect(wrapper.text()).toContain('Valid')
    expect(wrapper.text()).not.toContain('stale problem')
  })

  it('says so calmly when the server cannot be reached, without blocking the form', async () => {
    validate.mockRejectedValue(new Error('offline'))
    const wrapper = mount(ConditionEditor, { props: { modelValue: 'budget_used > 1' } })
    await flushPromises()
    expect(wrapper.text()).toContain('Couldn’t check the condition right now')
    expect(wrapper.emitted('status')?.at(-1)).toEqual(['unavailable'])
  })

  it('inserts a variable at the cursor and replaces the text with an example', async () => {
    validate.mockResolvedValue(valid())
    const wrapper = mount(ConditionEditor, { props: { modelValue: 'a  b' }, attachTo: document.body })
    await flushPromises()
    const element = wrapper.find('textarea').element as HTMLTextAreaElement
    element.setSelectionRange(2, 2)
    await wrapper.find('button[aria-label^="Insert budget_used"]').trigger('click')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['a budget_used b'])

    const example = wrapper.findAll('button').find((b) => b.text() === 'Budget nearly used')!
    await example.trigger('click')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['budget_used > 90'])
    wrapper.unmount()
  })

  it('shows a server field error under the field', async () => {
    validate.mockResolvedValue(valid())
    const wrapper = mount(ConditionEditor, { props: { modelValue: 'x', error: 'Fix the condition first.' } })
    await flushPromises()
    expect(wrapper.text()).toContain('Fix the condition first.')
  })
})
