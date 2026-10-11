import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ApiError } from '@/api/client'
import type { Provider } from '@/api/types'
import ProviderFormDrawer from './ProviderFormDrawer.vue'

const update = vi.fn()

vi.mock('@/stores/providers', () => ({ useProvidersStore: () => ({ update: (...a: unknown[]) => update(...a), create: vi.fn(), setDrained: vi.fn(), remove: vi.fn() }) }))

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
} as unknown as Provider

function mountForm() {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(ProviderFormDrawer, {
    props: { open: true, provider: saved },
    global: {
      plugins: [pinia],
      stubs: {
        UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' },
        DiscoverModelsDialog: { props: ['open'], template: '<div data-test="discover" :data-open="String(open)" />' },
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
      throw new ApiError(400, 'PUT', { detail: 'Kontrollera de markerade fälten.' })
    })
    const wrapper = mountForm()
    await wrapper.find('#prov-url').setValue('https://other.services.ai.azure.com/openai/v1')

    await discoverButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="discover"]').attributes('data-open')).toBe('false')
  })
})
