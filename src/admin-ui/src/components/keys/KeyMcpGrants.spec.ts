import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { McpGrant, McpServer, McpTool } from '@/api/types'
import { useMcpStore } from '@/stores/mcp'
import { button } from '@/test/helpers'
import KeyMcpGrants from './KeyMcpGrants.vue'

const mcpGrants = vi.fn()
const replaceMcpGrants = vi.fn()
const tools = vi.fn()

vi.mock('@/api', () => ({
  api: {
    keys: { mcpGrants: (...a: unknown[]) => mcpGrants(...a), replaceMcpGrants: (...a: unknown[]) => replaceMcpGrants(...a) },
    mcp: { tools: (...a: unknown[]) => tools(...a), list: vi.fn() },
  },
}))

const server = (id: string, slug: string): McpServer => ({ id, slug, displayName: slug.toUpperCase(), capabilities: ['Tools'], canManage: true }) as McpServer
const tool = (name: string): McpTool => ({ id: name, name, approvalState: 'Approved' }) as McpTool
const grant: McpGrant = {
  serverId: 's1', serverSlug: 'diarium', serverName: 'DIARIUM', allowedTools: ['search', 'future_tool'], approvalPolicy: 'NeverForReadOnly',
  allowResources: true, allowPrompts: false, unknownTools: ['future_tool'],
}

function mountGrants() {
  const pinia = createPinia()
  setActivePinia(pinia)
  const store = useMcpStore()
  store.servers = [server('s1', 'diarium'), server('s2', 'demo')]
  store.loaded = true
  return mount(KeyMcpGrants, { props: { keyId: 'k1', editable: true }, global: { plugins: [pinia] } })
}

describe('KeyMcpGrants', () => {
  beforeEach(() => {
    mcpGrants.mockReset().mockResolvedValue([grant])
    replaceMcpGrants.mockReset()
    tools.mockReset().mockImplementation(async (id: string) => (id === 's1' ? [tool('search'), tool('lookup')] : [tool('echo')]))
  })

  it('lists grants and warns about tool names the server does not offer', async () => {
    const wrapper = mountGrants()
    await flushPromises()
    expect(mcpGrants).toHaveBeenCalledWith('k1')
    expect(wrapper.text()).toContain('search, future_tool')
    expect(wrapper.text()).toContain('Not offered by the server: future_tool')
    expect(wrapper.text()).toContain('documents')
  })

  it('edits tools, adds a server and saves the whole list', async () => {
    replaceMcpGrants.mockImplementation(async (_: string, body: unknown[]) => body.map((g) => ({ ...(g as object), serverSlug: '', serverName: '', unknownTools: [] })))
    const wrapper = mountGrants()
    await flushPromises()
    await button(wrapper, 'Edit MCP access').trigger('click')
    await flushPromises()
    expect(tools).toHaveBeenCalledWith('s1')
    expect(wrapper.text()).toContain('Not offered by the server; allows nothing until it is.')

    const check = async (label: string) => {
      const input = wrapper.findAll('label').find((l) => l.text().startsWith(label))!.find('input')
      await input.setValue(!(input.element as HTMLInputElement).checked)
    }
    await check('lookup')
    await check('future_tool')
    const add = wrapper.findAll('select').at(-1)!
    await add.setValue('s2')
    await button(wrapper, 'Add').trigger('click')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(replaceMcpGrants).toHaveBeenCalledWith('k1', [
      { serverId: 's1', allowedTools: ['search', 'lookup'], approvalPolicy: 'NeverForReadOnly', allowResources: true, allowPrompts: false },
      { serverId: 's2', allowedTools: ['*'], approvalPolicy: 'Always', allowResources: false, allowPrompts: false },
    ])
    expect(wrapper.find('[aria-live=polite]').text()).toBe('MCP access saved.')
  })

  it('is read-only when the key cannot be edited', async () => {
    setActivePinia(createPinia())
    const wrapper = mount(KeyMcpGrants, { props: { keyId: 'k1', editable: false } })
    await flushPromises()
    expect(wrapper.findAll('button').some((b) => b.text().includes('Edit MCP access'))).toBe(false)
  })
})
