import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { VirtualKey } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import KeyDetailDrawer from './KeyDetailDrawer.vue'

const reveal = vi.fn()
vi.mock('@/api', () => ({ api: { keys: { reveal: (...a: unknown[]) => reveal(...a) }, budgets: { list: vi.fn().mockResolvedValue([]) } } }))

const SECRET = 'ume-sk-AbC1' + 'x'.repeat(32)

function key(overrides: Partial<VirtualKey> = {}): VirtualKey {
  return {
    id: 'k1', teamId: 't', teamName: 'Team', departmentId: 'd', departmentName: 'Dept', name: 'App', description: null, prefix: 'ume-sk-AbC1',
    status: 'Active', isEnabled: true, createdAt: '2026-10-01T00:00:00Z', createdBy: 'me', expiresAt: null, revokedAt: null, graceUntil: null,
    lastUsedAt: null, allowedModels: [], allowedResidencies: [], allowedProviders: ['ollama-cloud'], piiPolicy: 'Off', attachmentPolicy: 'Allowed',
    requestsPerMinute: null, tokensPerMinute: null, rotatedToKeyId: null, canReveal: true, ...overrides,
  } as VirtualKey
}

function mountDrawer(role: string, data: VirtualKey) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'n', email: null, roles: [role], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  return mount(KeyDetailDrawer, {
    props: { open: true, keyData: data },
    global: {
      plugins: [pinia],
      stubs: { UiDrawer: { template: '<div><slot name="badge" /><slot /><slot name="footer" /></div>' } },
    },
  })
}

describe('KeyDetailDrawer secret', () => {
  beforeEach(() => {
    reveal.mockReset()
    reveal.mockResolvedValue({ secret: SECRET })
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } })
  })

  it('shows the key masked by default and never reveals it without a click', async () => {
    const wrapper = mountDrawer('gateway-admin', key())
    await flushPromises()
    const text = wrapper.find('[data-testid="key-secret"]').text()
    expect(text.startsWith('ume-sk-AbC1')).toBe(true)
    expect(text).toContain('••••')
    expect(text).not.toContain('xxxx')
    expect(reveal).not.toHaveBeenCalled()
  })

  it('reveals through the audited endpoint, hides again and forgets when the drawer closes', async () => {
    const wrapper = mountDrawer('gateway-admin', key())
    await flushPromises()
    await wrapper.findAll('button').find((b) => b.text().endsWith('Show'))!.trigger('click')
    await flushPromises()
    expect(reveal).toHaveBeenCalledWith('k1', 'Reveal')
    expect(wrapper.find('[data-testid="key-secret"]').text()).toBe(SECRET)
    await wrapper.findAll('button').find((b) => b.text().endsWith('Hide'))!.trigger('click')
    expect(wrapper.find('[data-testid="key-secret"]').text()).not.toContain('xxxx')
    await wrapper.findAll('button').find((b) => b.text().endsWith('Show'))!.trigger('click')
    await flushPromises()
    await wrapper.setProps({ open: false })
    expect(wrapper.find('[data-testid="key-secret"]').text()).not.toContain('xxxx')
  })

  it('copy asks the server (audited as Copy) and writes the secret to the clipboard', async () => {
    const wrapper = mountDrawer('gateway-admin', key())
    await flushPromises()
    await wrapper.findAll('button').find((b) => b.text().endsWith('Copy'))!.trigger('click')
    await flushPromises()
    expect(reveal).toHaveBeenCalledWith('k1', 'Copy')
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(SECRET)
    expect(wrapper.find('[data-testid="key-secret"]').text()).not.toContain('xxxx')
  })

  it.each(['department-admin', 'viewer'])('does not offer the secret to %s', async (role) => {
    const wrapper = mountDrawer(role, key())
    await flushPromises()
    expect(wrapper.find('[data-testid="key-secret"]').exists()).toBe(false)
    expect(reveal).not.toHaveBeenCalled()
  })

  it('explains that older keys cannot be shown', async () => {
    const wrapper = mountDrawer('gateway-admin', key({ canReveal: false }))
    await flushPromises()
    expect(wrapper.find('[data-testid="key-secret"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Rotate it')
  })

  it('shows the key’s provider restriction', async () => {
    const wrapper = mountDrawer('gateway-admin', key())
    await flushPromises()
    expect(wrapper.text()).toContain('ollama-cloud')
  })
})
