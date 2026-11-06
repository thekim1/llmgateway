import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { DiscoveredModel, Model, Provider } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import DiscoverModelsDialog from './DiscoverModelsDialog.vue'

const discoverModels = vi.fn()
const create = vi.fn()
const update = vi.fn()
const items: Model[] = []

vi.mock('@/api', () => ({ api: { providers: { discoverModels: (...a: unknown[]) => discoverModels(...a) } } }))
vi.mock('@/stores/models', () => ({
  useModelsStore: () => ({
    items,
    load: async () => undefined,
    create: (...a: unknown[]) => create(...a),
    update: (...a: unknown[]) => update(...a),
    addPrice: vi.fn(),
  }),
}))

const provider = { id: 'p1', name: 'ai-foundry', type: 'AzureAIFoundry', residency: 'Eu' } as unknown as Provider

const discovered = (overrides: Partial<DiscoveredModel>): DiscoveredModel => ({
  id: 'Cohere-rerank-v5-fast',
  displayName: 'Cohere-rerank-v5-fast, version 1',
  kind: 'Rerank',
  parameterProfile: 'Standard',
  contextWindow: null,
  features: [],
  price: null,
  alreadyAdded: false,
  priceSource: null,
  description: null,
  baseModel: 'Cohere-rerank-v5-fast',
  publisher: 'Cohere',
  residency: 'Eu',
  ...overrides,
})

/** Opened after mounting, as the provider drawer does: opening starts discovery. */
async function mountDialog() {
  const pinia = createPinia()
  setActivePinia(pinia)
  const wrapper = mount(DiscoverModelsDialog, { props: { open: false, provider }, global: { plugins: [pinia], stubs: { UiDialog: inlineOverlay } } })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

const button = (wrapper: Awaited<ReturnType<typeof mountDialog>>, text: string) => wrapper.findAll('button').find((b) => b.text().startsWith(text))!

describe('DiscoverModelsDialog on Azure AI Foundry', () => {
  beforeEach(() => {
    items.length = 0
    discoverModels.mockReset()
    create.mockReset()
    update.mockReset()
    create.mockResolvedValue({})
    update.mockResolvedValue({})
  })

  it('adds a reranker with the kind, publisher and residency discovery suggests', async () => {
    discoverModels.mockResolvedValue([discovered({}), discovered({ id: 'gpt-4.1', kind: 'Chat', baseModel: 'gpt-4.1', publisher: 'OpenAI', residency: null })])
    const wrapper = await mountDialog()
    expect(wrapper.text()).toContain('EU (from the deployment type)')

    await wrapper.find('input[aria-label="Select Cohere-rerank-v5-fast"]').setValue(true)
    await wrapper.find('input[aria-label="Select gpt-4.1"]').setValue(true)
    await button(wrapper, 'Add 2 selected').trigger('click')
    await flushPromises()

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'Cohere-rerank-v5-fast', kind: 'Rerank', publisher: 'Cohere', residency: 'Eu' }))
    const chat = create.mock.calls.map((c) => c[0]).find((b) => b.name === 'gpt-4.1')
    expect(chat).toMatchObject({ kind: 'Chat', publisher: 'OpenAI' })
    expect(chat).not.toHaveProperty('residency') // a regional deployment: the provider's
  })

  it('updates a saved model from the provider without losing the residency and authentication the admin set', async () => {
    items.push({
      id: 'm1', providerId: 'p1', providerName: 'ai-foundry', name: 'Cohere-rerank-v5-fast', upstreamModel: 'Cohere-rerank-v5-fast', kind: 'Rerank',
      parameterProfile: 'Standard', contextWindow: null, isEnabled: true, features: [], currentPrice: null, description: null, baseModel: null,
      residency: 'External', residencyOverride: 'External', publisher: null, requestBaseUrl: null, requestAuthMode: 'Bearer', rerankBaseUrl: null,
    } as unknown as Model)
    discoverModels.mockResolvedValue([discovered({ alreadyAdded: true })])
    const wrapper = await mountDialog()
    expect(wrapper.text()).toContain('Publisher: Cohere')

    await button(wrapper, 'Update').trigger('click')
    await flushPromises()

    expect(update).toHaveBeenCalledWith('m1', expect.objectContaining({ publisher: 'Cohere', residency: 'External', requestAuthMode: 'Bearer' }))
  })
})
