import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ApiError } from '@/api/client'
import type { Provider } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import ProviderFormDrawer from './ProviderFormDrawer.vue'

const update = vi.fn()
const remove = vi.fn()
const loadModels = vi.fn()

vi.mock('@/stores/providers', () => ({
  useProvidersStore: () => ({ update: (...a: unknown[]) => update(...a), create: vi.fn(), setDrained: vi.fn(), remove: (...a: unknown[]) => remove(...a) }),
}))
vi.mock('@/stores/models', () => ({ useModelsStore: () => ({ loaded: true, load: () => loadModels() }) }))

const saved: Provider = {
  id: 'p1',
  name: 'ai-foundry',
  displayName: null,
  type: 'AzureAIFoundry',
  baseUrl: 'https://example-swc.services.ai.azure.com/openai/v1',
  authMode: 'ApiKeyHeader',
  residency: 'Eu',
  capabilities: ['ChatCompletions', 'Streaming'],
  timeoutSeconds: 120,
  isEnabled: true,
  isDrained: false,
  deploymentCount: 0,
  autoUpdatePrices: true,
  priceList: null,
} as unknown as Provider

function mountForm(provider: Provider = saved) {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(ProviderFormDrawer, {
    props: { open: true, provider },
    global: {
      plugins: [pinia],
      stubs: {
        UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' },
        DiscoverModelsDialog: { props: ['open'], template: '<div data-test="discover" :data-open="String(open)" />' },
        UiDialog: inlineOverlay,
      },
    },
  })
}

const discoverButton = (wrapper: ReturnType<typeof mountForm>) => wrapper.findAll('button').find((b) => b.text().includes('iscover models'))!

describe('ProviderFormDrawer discovery', () => {
  beforeEach(() => {
    update.mockReset()
  })

  it('opens discovery directly when nothing is unsaved', async () => {
    const wrapper = mountForm()
    expect(discoverButton(wrapper).text()).not.toContain('Save')

    await discoverButton(wrapper).trigger('click')

    expect(update).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="discover"]').attributes('data-open')).toBe('true')
  })

  it('saves a new URL and key before discovering, because discovery uses the saved provider', async () => {
    const projectUrl = 'https://myres.services.ai.azure.com/api/projects/proj'
    update.mockResolvedValue({ ...saved, baseUrl: 'https://myres.services.ai.azure.com/openai/v1' })
    const wrapper = mountForm()
    await wrapper.find('#prov-url').setValue(projectUrl)
    await wrapper.find('#prov-credential').setValue('secret')
    expect(discoverButton(wrapper).text()).toContain('Save and discover models')

    await discoverButton(wrapper).trigger('click')
    await flushPromises()

    expect(update).toHaveBeenCalledWith('p1', expect.objectContaining({ baseUrl: projectUrl, credential: 'secret' }))
    expect(wrapper.find('[data-test="discover"]').attributes('data-open')).toBe('true')
    expect(wrapper.emitted('update:open')).toBeUndefined()
  })

  it('does not discover when saving fails', async () => {
    update.mockImplementation(async () => {
      throw new ApiError(400, 'PUT', { detail: 'Check the highlighted fields.' })
    })
    const wrapper = mountForm()
    await wrapper.find('#prov-url').setValue('https://other.services.ai.azure.com/openai/v1')

    await discoverButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="discover"]').attributes('data-open')).toBe('false')
  })
})

describe('ProviderFormDrawer stored credential', () => {
  const withCredential = { ...saved, hasCredential: true } as Provider
  const submit = (wrapper: ReturnType<typeof mountForm>) => wrapper.find('form').trigger('submit')

  beforeEach(() => {
    update.mockReset()
    update.mockResolvedValue(withCredential)
  })

  it('asks for the credential again when the address moves to another host', async () => {
    const wrapper = mountForm(withCredential)
    await wrapper.find('#prov-url').setValue('https://elsewhere.example/openai/v1')

    await submit(wrapper)
    await flushPromises()

    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter the credential again')
  })

  it('keeps the stored credential for another path on the same host', async () => {
    const wrapper = mountForm(withCredential)
    await wrapper.find('#prov-url').setValue('https://example-swc.services.ai.azure.com/api/projects/p')

    await submit(wrapper)
    await flushPromises()

    expect(update).toHaveBeenCalledWith('p1', expect.not.objectContaining({ credential: expect.anything() }))
  })

  it('saves a new host together with a newly entered credential', async () => {
    const wrapper = mountForm(withCredential)
    await wrapper.find('#prov-url').setValue('https://elsewhere.example/openai/v1')
    await wrapper.find('#prov-credential').setValue('new-secret')

    await submit(wrapper)
    await flushPromises()

    expect(update).toHaveBeenCalledWith('p1', expect.objectContaining({ baseUrl: 'https://elsewhere.example/openai/v1', credential: 'new-secret' }))
  })
})

describe('ProviderFormDrawer endpoints', () => {
  beforeEach(() => {
    update.mockReset()
    update.mockResolvedValue(saved)
  })

  it('shows what a known provider type supports and sends no endpoints for it', async () => {
    const wrapper = mountForm()
    expect(wrapper.find('#prov-capabilities').exists()).toBe(false)
    expect(wrapper.text()).toContain('Supports Chat completions')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(update.mock.lastCall?.[1]).not.toHaveProperty('capabilities')
  })

  it('lets an OpenAI-compatible server choose its endpoints', async () => {
    const wrapper = mountForm({ ...saved, type: 'OpenAICompatible', capabilities: ['ChatCompletions', 'Streaming'] } as Provider)
    expect(wrapper.find('#prov-capabilities').exists()).toBe(true)
    expect(wrapper.text()).toContain('Endpoints this server offers')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(update.mock.lastCall?.[1]).toMatchObject({ capabilities: ['ChatCompletions', 'Streaming'] })
  })
})

describe('ProviderFormDrawer delete', () => {
  const button = (wrapper: ReturnType<typeof mountForm>, text: string) => wrapper.findAll('button').find((b) => b.text() === text)!

  beforeEach(() => {
    remove.mockReset()
    remove.mockResolvedValue(undefined)
    loadModels.mockReset()
  })

  it('deletes a provider without models on its own', async () => {
    const wrapper = mountForm()
    await button(wrapper, 'Delete').trigger('click')
    await button(wrapper, 'Delete provider').trigger('click')
    await flushPromises()

    expect(remove).toHaveBeenCalledWith('p1', false)
    expect(loadModels).not.toHaveBeenCalled()
  })

  it('deletes a provider together with its models and reloads the models', async () => {
    const wrapper = mountForm({ ...saved, deploymentCount: 3 })
    expect(button(wrapper, 'Delete').attributes('disabled')).toBeUndefined()
    await button(wrapper, 'Delete').trigger('click')
    expect(wrapper.text()).toContain('and its 3 models are removed for good')

    await button(wrapper, 'Delete provider and 3 models').trigger('click')
    await flushPromises()

    expect(remove).toHaveBeenCalledWith('p1', true)
    expect(loadModels).toHaveBeenCalled()
    expect(wrapper.emitted('deleted')).toHaveLength(1)
  })

  it('shows why the models cannot be deleted', async () => {
    remove.mockImplementation(async () => {
      throw new ApiError(409, 'DELETE', { detail: "The provider's models are used in the routes ume/chat. Remove them from the routes first." })
    })
    const wrapper = mountForm({ ...saved, deploymentCount: 2 })
    await button(wrapper, 'Delete').trigger('click')
    await button(wrapper, 'Delete provider and 2 models').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('used in the routes ume/chat')
    expect(wrapper.emitted('deleted')).toBeUndefined()
  })
})
