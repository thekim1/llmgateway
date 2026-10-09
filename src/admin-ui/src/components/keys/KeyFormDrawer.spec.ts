import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import type { Provider, VirtualKey } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useProvidersStore } from '@/stores/providers'
import KeyFormDrawer from './KeyFormDrawer.vue'

const create = vi.fn()
const update = vi.fn()

vi.mock('@/composables/useModelOptions', () => ({
  useModelOptions: () => ({
    options: ref([
      { name: 'ollama-cloud/gpt-oss', source: 'model', residencies: ['External'], providers: ['ollama-cloud'] },
      { name: 'openai/gpt-4.1-mini', source: 'model', residencies: ['External'], providers: ['openai'] },
      { name: 'ume/chat', source: 'route', residencies: ['External'], providers: ['ollama-cloud', 'openai'] },
      { name: 'ume/openai-only', source: 'route', residencies: ['External'], providers: ['openai'] },
    ]),
    loading: ref(false),
    error: ref(null),
    load: vi.fn(),
  }),
}))
vi.mock('@/stores/teams', () => ({ useTeamsStore: () => ({ items: [{ id: 't1', name: 'Team', departmentName: 'Dept' }], loading: false, error: null, load: vi.fn() }) }))
vi.mock('@/stores/keys', () => ({ useKeysStore: () => ({ create: (...a: unknown[]) => create(...a), update: (...a: unknown[]) => update(...a) }) }))

function provider(name: string): Provider {
  return { id: name, name, displayName: name, residency: 'External' } as Provider
}

function mountForm(existing: VirtualKey | null = null, role = 'gateway-admin') {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'n', email: null, roles: [role], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  const providers = useProvidersStore()
  providers.items = [provider('ollama-cloud'), provider('openai')]
  providers.loaded = true
  return mount(KeyFormDrawer, {
    props: { open: true, existing },
    global: {
      plugins: [pinia],
      stubs: { UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' } },
    },
  })
}

const labels = (wrapper: ReturnType<typeof mountForm>, legend: string) =>
  wrapper.findAll(`fieldset:has(legend)`).filter((f) => f.find('legend').text() === legend).flatMap((f) => f.findAll('label').map((l) => l.text()))

async function checkbox(wrapper: ReturnType<typeof mountForm>, label: string) {
  const input = wrapper.findAll('label').find((l) => l.text().startsWith(label))!.find('input')
  await input.setValue(!(input.element as HTMLInputElement).checked)
}

describe('KeyFormDrawer provider then model selection', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
  })

  it('asks for providers before models and lists every model while all providers are allowed', async () => {
    const wrapper = mountForm()
    await flushPromises()
    const html = wrapper.html()
    expect(html.indexOf('Allow all providers')).toBeGreaterThan(-1)
    expect(html.indexOf('Allow all providers')).toBeLessThan(html.indexOf('Allow all models'))
    await checkbox(wrapper, 'Allow all models')
    const models = labels(wrapper, 'Allowed models').join(' ')
    for (const name of ['ollama-cloud/gpt-oss', 'openai/gpt-4.1-mini', 'ume/chat', 'ume/openai-only']) expect(models).toContain(name)
  })

  it('only shows models from the selected providers', async () => {
    const wrapper = mountForm()
    await flushPromises()
    await checkbox(wrapper, 'Allow all providers')
    await checkbox(wrapper, 'ollama-cloud')
    await checkbox(wrapper, 'Allow all models')
    const models = labels(wrapper, 'Allowed models').join(' ')
    expect(models).toContain('ollama-cloud/gpt-oss')
    expect(models).toContain('ume/chat') // a route that has a target on the selected provider
    expect(models).not.toContain('openai/gpt-4.1-mini')
    expect(models).not.toContain('ume/openai-only')
  })

  it('drops already chosen models when their provider is deselected', async () => {
    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: ['ollama-cloud/gpt-oss', 'openai/gpt-4.1-mini'], allowedResidencies: [], allowedProviders: ['ollama-cloud', 'openai'], piiPolicy: 'Off',
      requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
    } as unknown as VirtualKey
    const wrapper = mountForm(existing)
    await flushPromises()
    await checkbox(wrapper, 'openai')
    await flushPromises()
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const body = update.mock.calls[0]![1] as { allowedModels: string[]; allowedProviders: string[] }
    expect(body.allowedProviders).toEqual(['ollama-cloud'])
    expect(body.allowedModels).toEqual(['ollama-cloud/gpt-oss'])
  })

  it('requires at least one provider when not allowing all', async () => {
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('input[autocomplete=off]').setValue('My key')
    await checkbox(wrapper, 'Allow all providers')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Select at least one provider')
  })

  it('does not touch providers for roles that cannot list them', async () => {
    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: ['openai'], piiPolicy: 'Off', requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
    } as unknown as VirtualKey
    const wrapper = mountForm(existing, 'department-admin')
    await flushPromises()
    expect(wrapper.text()).not.toContain('Allow all providers')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls[0]![1]).not.toHaveProperty('allowedProviders')
  })

  it('sends the chosen attachment policy and keeps the existing one on edit', async () => {
    create.mockResolvedValue({ key: {}, secret: 's' })
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('select').setValue('t1')
    await wrapper.find('input[autocomplete=off]').setValue('Sensitive key')
    expect(wrapper.find('[role=radiogroup][aria-label="Attached files"] [aria-checked=true]').text()).toBe('All files')
    expect(wrapper.text()).not.toContain('A safeguard, not a guarantee')
    await wrapper.findAll('[role=radiogroup][aria-label="Attached files"] [role=radio]').find((b) => b.text() === 'Text only')!.trigger('click')
    expect(wrapper.text()).toContain('Any attached file is rejected')
    expect(wrapper.text()).toContain('A safeguard, not a guarantee')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create.mock.calls[0]![0]).toMatchObject({ attachmentPolicy: 'None' })

    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: [], piiPolicy: 'Off', attachmentPolicy: 'ImagesOnly', requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
    } as unknown as VirtualKey
    const edit = mountForm(existing)
    await flushPromises()
    await edit.find('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls[0]![1]).toMatchObject({ attachmentPolicy: 'ImagesOnly' })
  })
})
