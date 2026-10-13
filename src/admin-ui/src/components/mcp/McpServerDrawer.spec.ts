import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { McpServer } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { button } from '@/test/helpers'
import McpServerDrawer from './McpServerDrawer.vue'

const create = vi.fn()
const update = vi.fn()
const test = vi.fn()

vi.mock('@/stores/mcp', async (original) => ({
  ...(await original<typeof import('@/stores/mcp')>()),
  useMcpStore: () => ({ create: (...a: unknown[]) => create(...a), update: (...a: unknown[]) => update(...a), test: (...a: unknown[]) => test(...a), remove: vi.fn() }),
}))
vi.mock('@/stores/departments', () => ({
  useDepartmentsStore: () => ({ items: [{ id: 'd1', name: 'Socialtjänsten' }], loaded: true, load: vi.fn() }),
}))

const stored: McpServer = {
  id: 's1', slug: 'diarium', displayName: 'Diarium', description: null, url: 'https://diarium.example/mcp', residency: 'Eu', isEnabled: true,
  ownerDepartmentId: null, ownerDepartmentName: null, authType: 'StaticHeaders', hasCredential: true, forwardHeaders: ['X-Trace'],
  timeoutSeconds: 30, maxResultBytes: 1048576, autoApproveNewTools: false, status: 'Healthy', lastCheckedAt: null, lastError: null,
  capabilities: ['Tools'], instructions: null, createdAt: '2026-10-12T08:00:00Z', toolCount: 0, pendingToolCount: 0, changedToolCount: 0, canManage: true,
}

function mountDrawer(server: McpServer | null = null, role = 'gateway-admin') {
  const pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { isAuthenticated: true, name: 'n', email: null, roles: [role], departmentCodes: [], sessionExpiresAt: null }
  auth.status = 'authenticated'
  const wrapper = mount(McpServerDrawer, {
    props: { open: true, server },
    global: {
      plugins: [pinia],
      stubs: {
        UiDrawer: { template: '<div><slot name="badge" /><slot /><slot name="footer" /></div>' },
        McpToolsPanel: { template: '<div data-test="tools" />' },
        ConfirmDialog: true,
      },
    },
  })
  return wrapper
}

async function settings(wrapper: ReturnType<typeof mountDrawer>) {
  const tab = wrapper.findAll('button').find((b) => b.text() === 'Settings')
  if (tab) await tab.trigger('click')
}

async function fill(wrapper: ReturnType<typeof mountDrawer>, slug = 'case_files') {
  await wrapper.find('#mcp-slug').setValue(slug)
  await wrapper.find('#mcp-display').setValue('Case files')
  await wrapper.find('#mcp-url').setValue('http://cases.internal/mcp')
}

describe('McpServerDrawer', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
    test.mockReset()
  })

  it('shows slug problems while typing and does not create an invalid server', async () => {
    const wrapper = mountDrawer()
    await wrapper.find('#mcp-slug').setValue('Case__files')
    expect(wrapper.text()).toContain('Use lower-case letters and digits')
    expect(wrapper.find('#mcp-slug').attributes('aria-invalid')).toBe('true')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()

    await fill(wrapper)
    expect(wrapper.text()).toContain('Tools appear as case_files__tool on /mcp')
    create.mockResolvedValue({ ...stored, id: 'new', slug: 'case_files' })
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ slug: 'case_files', url: 'http://cases.internal/mcp', residency: 'OnPrem', ownerDepartmentId: null }))
    expect(create.mock.calls[0]![0]).not.toHaveProperty('credential')
    expect(wrapper.emitted('created')).toHaveLength(1)
  })

  it('sends static headers once and never shows a stored credential', async () => {
    const wrapper = mountDrawer(stored)
    await settings(wrapper)
    expect(wrapper.find('[data-testid=credential-state]').text()).toContain('Set.')
    expect(wrapper.find('input[type=password]').exists()).toBe(false)

    update.mockResolvedValue(stored)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls[0]![1]).not.toHaveProperty('credential')
    expect(update.mock.calls[0]![1]).toMatchObject({ forwardHeaders: ['X-Trace'], isEnabled: true })

    await wrapper.findAll('label').find((l) => l.text().includes('Replace the stored credential'))!.find('input').setValue(true)
    const inputs = wrapper.findAll('#mcp-credential input')
    await inputs[0]!.setValue('X-Api-Key')
    await inputs[1]!.setValue('s3cret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update.mock.calls[1]![1]).toMatchObject({ authType: 'StaticHeaders', credential: { headers: { 'X-Api-Key': 's3cret' } } })
  })

  it('needs new credentials when the URL moves to another host, but not for another path', async () => {
    const wrapper = mountDrawer(stored)
    await settings(wrapper)
    await wrapper.find('#mcp-url').setValue('https://diarium.example/v2/mcp')
    expect(wrapper.find('[data-testid=credential-state]').text()).toContain('Set.')

    await wrapper.find('#mcp-url').setValue('https://elsewhere.example/mcp')
    expect(wrapper.find('[data-testid=credential-state]').text()).toContain('not sent to a new host')
    expect(wrapper.findAll('#mcp-credential input').length).toBeGreaterThan(0)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
  })

  it('needs new credentials when the auth type changes, and refuses protocol headers', async () => {
    const wrapper = mountDrawer(stored)
    await settings(wrapper)
    await wrapper.find('#mcp-auth').setValue('OAuthClientCredentials')
    expect(wrapper.find('[data-testid=credential-state]').text()).toContain('Not set for this authentication type')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter the client ID and client secret.')

    await wrapper.find('#mcp-auth').setValue('StaticHeaders')
    await wrapper.findAll('label').find((l) => l.text().includes('Replace the stored credential'))!.find('input').setValue(true)
    const inputs = wrapper.findAll('#mcp-credential input')
    await inputs[0]!.setValue('Host')
    await inputs[1]!.setValue('x')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Host belongs to HTTP or MCP')
  })

  it('warns about auto-approval', async () => {
    const wrapper = mountDrawer()
    expect(wrapper.text()).not.toContain('New tools reach keys without review')
    await wrapper.findAll('label').find((l) => l.text().includes('Approve new tools automatically'))!.find('input').setValue(true)
    expect(wrapper.text()).toContain('New tools reach keys without review')
  })

  it('tests an unsaved server and announces the result', async () => {
    test.mockResolvedValue({ succeeded: true, error: null, httpStatus: null, capabilities: ['Tools'], instructions: null, tools: [{ name: 'search', title: null, description: null, exposedName: 'case_files__search' }], warnings: [] })
    const wrapper = mountDrawer()
    await fill(wrapper)
    await button(wrapper, 'Test connection').trigger('click')
    await flushPromises()
    expect(test).toHaveBeenCalledWith(null, expect.objectContaining({ slug: 'case_files' }))
    const region = wrapper.find('[data-testid=mcp-test-result]')
    expect(region.attributes('aria-live')).toBe('polite')
    expect(region.text()).toContain('Connected. The server offers 1 tool')
    expect(region.text()).toContain('search')
  })

  it('tests a saved server with its unsaved changes and shows a failure', async () => {
    test.mockResolvedValue({ succeeded: false, error: 'Servern nekade gatewayens autentiseringsuppgifter (HTTP 401).', httpStatus: 401, capabilities: [], instructions: null, tools: [], warnings: [] })
    const wrapper = mountDrawer(stored)
    await settings(wrapper)
    await button(wrapper, 'Test connection').trigger('click')
    await flushPromises()
    expect(test).toHaveBeenCalledWith('s1', expect.not.objectContaining({ credential: expect.anything() }))
    expect(wrapper.find('[data-testid=mcp-test-result]').text()).toContain('HTTP 401')
  })

  it('shows global servers read-only to department admins', async () => {
    const wrapper = mountDrawer({ ...stored, canManage: false }, 'department-admin')
    expect(wrapper.text()).toContain('managed by gateway administrators')
    await settings(wrapper)
    expect(wrapper.findAll('button').some((b) => b.text().includes('Save changes'))).toBe(false)
    expect(wrapper.find('#mcp-slug').element.matches(':disabled')).toBe(true)
  })
})
