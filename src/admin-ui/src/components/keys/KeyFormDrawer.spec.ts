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
const integrations = vi.hoisted(() => ({ items: [] as unknown[], loaded: true, loading: false, load: () => Promise.resolve(null) }))
vi.mock('@/stores/integrations', () => ({ useIntegrationsStore: () => integrations }))

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

  it('leaves provider tools and storage off for a new key and keeps them on edit', async () => {
    create.mockResolvedValue({ key: {}, secret: 's' })
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('select').setValue('t1')
    await wrapper.find('input[autocomplete=off]').setValue('Plain key')
    expect(wrapper.text()).toContain('Allow provider tools')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create.mock.calls[0]![0]).toMatchObject({ allowProviderTools: false, allowProviderStorage: false, requireMaxResidency: false })

    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: [], piiPolicy: 'Off', attachmentPolicy: 'Allowed', requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
      allowProviderTools: true, allowProviderStorage: false, requireMaxResidency: true,
    } as unknown as VirtualKey
    const edit = mountForm(existing)
    await flushPromises()
    await edit.find('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls[0]![1]).toMatchObject({ allowProviderTools: true, allowProviderStorage: false, requireMaxResidency: true })
  })
})

describe('KeyFormDrawer client addresses', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
  })

  it('sends the address list one entry per line and shows the existing one on edit', async () => {
    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: [], allowedIpRanges: ['203.0.113.0/24'], piiPolicy: 'Off',
      requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
    } as unknown as VirtualKey
    const wrapper = mountForm(existing, 'department-admin')
    await flushPromises()
    const field = wrapper.find('textarea#key-addresses')
    expect((field.element as HTMLTextAreaElement).value).toBe('203.0.113.0/24')
    await field.setValue('203.0.113.0/24\n 2001:db8::10 \n')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect((update.mock.calls[0]![1] as { allowedIpRanges: string[] }).allowedIpRanges).toEqual(['203.0.113.0/24', '2001:db8::10'])
  })

  it('refuses an invalid entry before saving', async () => {
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('input[autocomplete=off]').setValue('My key')
    await wrapper.find('textarea#key-addresses').setValue('10.20.1.5/16')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Did you mean 10.20.0.0/16?')
  })

  it('sends an empty list when no addresses are entered', async () => {
    create.mockResolvedValue({ key: {}, secret: 's' })
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('select').setValue('t1')
    await wrapper.find('input[autocomplete=off]').setValue('My key')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect((create.mock.calls[0]![0] as { allowedIpRanges: string[] }).allowedIpRanges).toEqual([])
  })
})

describe('KeyFormDrawer integrations', () => {
  const rag = { id: 'i1', name: 'Ume.Rag', isActive: true, ownerTeamId: 't1', ownerTeamName: 'Team' }

  beforeEach(() => {
    create.mockReset()
    update.mockReset()
    create.mockResolvedValue({ key: {}, secret: null })
    integrations.items = [rag, { id: 'i2', name: 'Paused', isActive: false, ownerTeamId: 't1', ownerTeamName: 'Team' }]
  })

  it('has no integration section when there are no integrations', async () => {
    integrations.items = []
    const wrapper = mountForm()
    await flushPromises()
    expect(wrapper.find('#key-integration').exists()).toBe(false)
  })

  it('lets a department admin assign a new key to an active integration, without a role choice', async () => {
    const wrapper = mountForm(null, 'department-admin')
    await flushPromises()
    const options = wrapper.findAll('#key-integration option').map((o) => o.text())
    expect(options).toEqual(['No integration', 'Ume.Rag']) // not the deactivated one
    await wrapper.find('#key-team').setValue('t1')
    await wrapper.find('input[autocomplete=off]').setValue('Chatbot')
    await wrapper.find('#key-integration').setValue('i1')
    expect(wrapper.find('[role=radiogroup][aria-label="Role in the integration"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('It has no secret')
    expect(wrapper.text()).toContain('embedding and rerank models included')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const body = create.mock.calls[0]![0]
    expect(body).toMatchObject({ teamId: 't1', integrationId: 'i1', integrationRole: 'Assigned' })
    expect(body).not.toHaveProperty('integrationScopes')
  })

  it('lets a gateway admin make a credential key in the owner team with scopes', async () => {
    const wrapper = mountForm()
    await flushPromises()
    await wrapper.find('input[autocomplete=off]').setValue('Ume.Rag api')
    await wrapper.find('#key-integration').setValue('i1')
    await wrapper.findAll('[role=radiogroup][aria-label="Role in the integration"] [role=radio]').find((b) => b.text() === 'Credential')!.trigger('click')
    await flushPromises()
    expect((wrapper.find('#key-team').element as HTMLSelectElement).value).toBe('t1')
    expect((wrapper.find('#key-team').element as HTMLSelectElement).disabled).toBe(true)
    expect(wrapper.text()).toContain('an upper bound on every key this key is used for')
    expect(wrapper.text()).not.toContain('embedding and rerank models included')
    await checkbox(wrapper, 'Read usage')
    await checkbox(wrapper, 'Read the organisation')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create.mock.calls[0]![0]).toMatchObject({ teamId: 't1', integrationId: 'i1', integrationRole: 'Credential', integrationScopes: ['usage.read', 'organisation.read'] })
  })

  it('shows an assigned key’s integration on edit and does not send it again', async () => {
    update.mockResolvedValue({})
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'Chatbot', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: [], piiPolicy: 'Off', requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
      integrationId: 'i1', integrationName: 'Ume.Rag', integrationRole: 'Assigned', integrationScopes: [],
    } as unknown as VirtualKey
    const wrapper = mountForm(existing, 'department-admin')
    await flushPromises()
    expect(wrapper.text()).toContain('Used through Ume.Rag as assigned key')
    expect(wrapper.find('#key-integration').exists()).toBe(false)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const body = update.mock.calls[0]![1]
    expect(body).not.toHaveProperty('integrationId')
    expect(body).not.toHaveProperty('integrationScopes')
  })

  it('does not let a department admin save an integration’s credential key', async () => {
    const existing = {
      id: 'k1', teamId: 't1', teamName: 'Team', departmentName: 'Dept', name: 'Ume.Rag api', description: null, prefix: 'ume-sk-x', status: 'Active', isEnabled: true,
      allowedModels: [], allowedResidencies: [], allowedProviders: [], piiPolicy: 'Off', requestsPerMinute: null, tokensPerMinute: null, expiresAt: null,
      integrationId: 'i1', integrationName: 'Ume.Rag', integrationRole: 'Credential', integrationScopes: ['usage.read'],
    } as unknown as VirtualKey
    const wrapper = mountForm(existing, 'department-admin')
    await flushPromises()
    expect(wrapper.text()).toContain('Only gateway admins can change it')
    expect(wrapper.find('button[type=submit]').attributes('aria-disabled')).toBe('true')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
  })
})
