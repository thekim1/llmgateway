import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ApiError } from '@/api/client'
import type { McpServer, McpTool } from '@/api/types'
import { button } from '@/test/helpers'
import { inlineOverlay } from '@/test/fixtures'
import McpToolDialog from './McpToolDialog.vue'

const approve = vi.fn()
const reject = vi.fn()
const setPrice = vi.fn()

vi.mock('@/api', () => ({
  api: { mcp: { approve: (...a: unknown[]) => approve(...a), reject: (...a: unknown[]) => reject(...a), setPrice: (...a: unknown[]) => setPrice(...a) } },
}))

const server = { id: 's1', slug: 'diarium', canManage: true } as McpServer
const changed: McpTool = {
  id: 't1', serverId: 's1', name: 'search', title: null, exposedName: 'diarium__search', approvalState: 'Changed', isExposed: true,
  definition: { description: 'Searches and deletes cases.' }, definitionHash: 'b'.repeat(64), approvedDefinition: { description: 'Searches cases.' },
  priceSek: null, discoveredAt: '2026-10-12T08:00:00Z', approvedAt: '2026-10-12T08:00:00Z', approvedBy: 'admin',
  changes: [{ path: '/description', kind: 'Changed', before: 'Searches cases.', after: 'Searches and deletes cases.' }],
}

function mountDialog(tool: McpTool = changed) {
  setActivePinia(createPinia())
  return mount(McpToolDialog, { props: { open: true, server, tool }, global: { stubs: { UiDialog: inlineOverlay } } })
}

describe('McpToolDialog', () => {
  beforeEach(() => {
    approve.mockReset()
    reject.mockReset()
    setPrice.mockReset()
  })

  it('shows what changed as text, approved next to offered', () => {
    const wrapper = mountDialog()
    const diff = wrapper.find('ol[aria-label="Changes from the approved definition"]')
    expect(diff.text()).toContain('Changed')
    expect(diff.text()).toContain('/description')
    expect(diff.text()).toContain('Approved (what keys see)')
    expect(diff.text()).toContain('Searches cases.')
    expect(diff.text()).toContain('Offered now (needs approval)')
    expect(diff.text()).toContain('Searches and deletes cases.')
  })

  it('approves exactly the reviewed definition', async () => {
    approve.mockResolvedValue({ ...changed, approvalState: 'Approved', changes: [] })
    const wrapper = mountDialog()
    await button(wrapper, 'Approve the change').trigger('click')
    await flushPromises()
    expect(approve).toHaveBeenCalledWith('s1', 't1', 'b'.repeat(64))
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('update:open')?.[0]).toEqual([false])
  })

  it('reloads and explains when the definition changed again (409)', async () => {
    approve.mockRejectedValue(new ApiError(409, 'POST', { detail: 'Verktygets definition har ändrats sedan den granskades.' }))
    const wrapper = mountDialog()
    await button(wrapper, 'Approve the change').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('stale')).toHaveLength(1)
    expect(wrapper.emitted('update:open')).toBeUndefined()
    const live = wrapper.find('[aria-live=polite]')
    expect(live.text()).toContain('nothing was approved')
  })

  it('saves a price, empty meaning free', async () => {
    setPrice.mockResolvedValue(changed)
    const wrapper = mountDialog({ ...changed, priceSek: 2 })
    await wrapper.find('form input').setValue('1,5')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(setPrice).toHaveBeenCalledWith('s1', 't1', 1.5)
    await wrapper.find('form input').setValue('')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(setPrice).toHaveBeenLastCalledWith('s1', 't1', null)
  })

  it('offers no actions on servers the user cannot manage', () => {
    setActivePinia(createPinia())
    const wrapper = mount(McpToolDialog, { props: { open: true, server: { ...server, canManage: false }, tool: changed }, global: { stubs: { UiDialog: inlineOverlay } } })
    expect(wrapper.findAll('button').map((b) => b.text())).toEqual(['Close'])
  })
})
