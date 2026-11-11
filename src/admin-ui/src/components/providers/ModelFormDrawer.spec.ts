import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Model, Provider } from '@/api/types'
import ModelFormDrawer from './ModelFormDrawer.vue'

const create = vi.fn()
const update = vi.fn()

vi.mock('@/stores/models', () => ({
  useModelsStore: () => ({
    create: (...a: unknown[]) => create(...a),
    update: (...a: unknown[]) => update(...a),
    remove: vi.fn(),
    prices: async () => [],
  }),
}))
vi.mock('@/stores/ui', () => ({ useUiStore: () => ({ notify: vi.fn() }) }))

const foundry = {
  id: 'p1',
  name: 'ai-foundry',
  displayName: null,
  type: 'AzureAIFoundry',
  baseUrl: 'https://umea-res.services.ai.azure.com/api/projects/umea',
  authMode: 'ApiKeyHeader',
  residency: 'External',
  priceList: 'azure',
  autoUpdatePrices: true,
} as unknown as Provider

const compatible = { ...foundry, id: 'p2', name: 'vllm', type: 'OpenAICompatible', residency: 'OnPrem', priceList: null } as unknown as Provider

const reranker = {
  id: 'm1',
  providerId: 'p1',
  providerName: 'ai-foundry',
  residency: 'Eu',
  name: 'Cohere-rerank-v5-fast',
  upstreamModel: 'Cohere-rerank-v5-fast',
  kind: 'Rerank',
  parameterProfile: 'Standard',
  contextWindow: null,
  isEnabled: true,
  features: [],
  currentPrice: null,
  publicName: 'Cohere-rerank-v5-fast',
  description: null,
  autoUpdatePrice: false,
  priceMode: 'Manual',
  priceListKey: null,
  baseModel: null,
  residencyOverride: 'Eu',
  publisher: 'Cohere',
  requestBaseUrl: null,
  requestAuthMode: null,
  rerankBaseUrl: 'https://umea-res.services.ai.azure.com/providers/cohere/v2',
} as Model

function mountForm(model: Model | null, providers: Provider[] = [foundry, compatible]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(ModelFormDrawer, {
    props: { open: true, model, providers },
    global: {
      plugins: [pinia],
      stubs: {
        UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' },
        AddPriceDialog: true,
        ConfirmDialog: true,
      },
    },
  })
}

const submit = async (wrapper: ReturnType<typeof mountForm>) => {
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('ModelFormDrawer residency and request address', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
    create.mockResolvedValue(reranker)
    update.mockResolvedValue(reranker)
  })

  it('shows a Foundry reranker’s own residency, publisher and where it is called', () => {
    const wrapper = mountForm(reranker)
    expect((wrapper.find('#model-residency').element as HTMLSelectElement).value).toBe('Eu')
    expect(wrapper.find('#model-residency').text()).toContain('Provider’s (External)')
    expect((wrapper.find('#model-publisher').element as HTMLInputElement).value).toBe('Cohere')
    expect(wrapper.find('#model-request-url').exists()).toBe(true)
    expect(wrapper.text()).toContain('Requests go to https://umea-res.services.ai.azure.com/providers/cohere/v2/rerank')
    expect(wrapper.find('#model-request-auth').text()).toContain('As the provider (API key header)')
  })

  it('saves the residency, publisher, request address and authentication the admin chose', async () => {
    const wrapper = mountForm(reranker)
    await wrapper.find('#model-residency').setValue('External')
    await wrapper.find('#model-request-url').setValue(' https://umea-res.services.ai.azure.com/providers/cohere/v2 ')
    await wrapper.find('#model-request-auth').setValue('Bearer')

    await submit(wrapper)

    expect(update).toHaveBeenCalledWith(
      'm1',
      expect.objectContaining({
        residency: 'External',
        publisher: 'Cohere',
        requestBaseUrl: 'https://umea-res.services.ai.azure.com/providers/cohere/v2',
        requestAuthMode: 'Bearer',
      }),
    )
  })

  it('sends null for the provider’s residency and authentication, so a PUT clears them', async () => {
    const wrapper = mountForm({ ...reranker, requestAuthMode: 'Bearer' })
    await wrapper.find('#model-residency').setValue('')
    await wrapper.find('#model-request-auth').setValue('')

    await submit(wrapper)

    expect(update).toHaveBeenCalledWith('m1', expect.objectContaining({ residency: null, requestAuthMode: null, requestBaseUrl: '' }))
  })

  it('refuses a request address that is not absolute before sending', async () => {
    const wrapper = mountForm(reranker)
    await wrapper.find('#model-request-url').setValue('providers/cohere/v2')

    await submit(wrapper)

    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter an absolute address')
  })

  it('warns when a saved Foundry reranker has no request address', () => {
    const wrapper = mountForm({ ...reranker, publisher: null, rerankBaseUrl: null })
    expect(wrapper.text()).toContain('has no request address, so it is not routed')
  })

  it('has no request address for a chat model and removes any left over', async () => {
    const wrapper = mountForm({ ...reranker, kind: 'Chat', residencyOverride: null, residency: 'External', rerankBaseUrl: null })
    expect(wrapper.find('#model-request-url').exists()).toBe(false)
    expect(wrapper.find('#model-publisher').exists()).toBe(true) // still a Foundry deployment

    await submit(wrapper)

    expect(update).toHaveBeenCalledWith('m1', expect.objectContaining({ kind: 'Chat', residency: null, requestBaseUrl: '', requestAuthMode: null }))
  })

  it('offers a residency for a model of any provider, but no publisher outside Foundry', async () => {
    const wrapper = mountForm(null, [compatible])
    expect(wrapper.find('#model-publisher').exists()).toBe(false)
    expect(wrapper.find('#model-residency').text()).toContain('Provider’s (On-prem)')
    await wrapper.find('#model-name').setValue('bge-reranker')
    await wrapper.find('#model-upstream').setValue('BAAI/bge-reranker-v2-m3')
    await wrapper.find('#model-kind').setValue('Rerank')
    await wrapper.find('#model-residency').setValue('Eu')
    expect(wrapper.find('#model-request-url').exists()).toBe(false) // an OpenAI-compatible reranker uses the provider's address

    await submit(wrapper)

    const body = create.mock.lastCall?.[0]
    expect(body).toMatchObject({ providerId: 'p2', kind: 'Rerank', residency: 'Eu', requestBaseUrl: '', requestAuthMode: null })
    expect(body).not.toHaveProperty('publisher')
  })
})

describe('ModelFormDrawer output limit', () => {
  beforeEach(() => {
    update.mockReset()
    update.mockResolvedValue(reranker)
  })

  it('sends the output limit and refuses one larger than the context window', async () => {
    const wrapper = mountForm({ ...reranker, contextWindow: 8000, maxOutputTokens: 2000 })
    expect((wrapper.find('#model-max-output').element as HTMLInputElement).value).toBe('2000')
    await wrapper.find('#model-max-output').setValue('9000')
    await submit(wrapper)
    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('The output limit cannot be larger than the context window.')

    await wrapper.find('#model-max-output').setValue('4000')
    await submit(wrapper)
    expect(update).toHaveBeenCalledWith('m1', expect.objectContaining({ contextWindow: 8000, maxOutputTokens: 4000 }))
  })

  it('sends null when the output limit is cleared, so a PUT removes it', async () => {
    const wrapper = mountForm({ ...reranker, maxOutputTokens: 2000 })
    await wrapper.find('#model-max-output').setValue('')
    await submit(wrapper)
    expect(update).toHaveBeenCalledWith('m1', expect.objectContaining({ maxOutputTokens: null }))
  })
})
