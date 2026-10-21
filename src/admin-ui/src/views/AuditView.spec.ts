import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { AuditEntry } from '@/api/types'
import AuditView from './AuditView.vue'

const entries: AuditEntry[] = [
  {
    id: '2', timestamp: '2026-10-21T08:00:00Z', actor: 'f3a1-subject', action: 'export', entityType: 'Usage', entityId: 'usage.csv',
    details: { before: null, after: { rows: 12 } }, actorName: 'Anna Admin', actorRoles: 'gateway-admin', actorDepartments: 'IT',
    sourceAddress: '192.0.2.10', sessionId: 'a1b2c3', requestId: '4bf92f35', userAgent: 'Mozilla/5.0',
  },
  { id: '1', timestamp: '2026-10-20T08:00:00Z', actor: 'system:seed', action: 'seed', entityType: 'Database', entityId: null, details: 'Demo data created' },
]

vi.mock('@/api', () => ({
  api: { audit: { list: () => Promise.resolve({ items: entries, total: entries.length }) } },
}))

describe('AuditView', () => {
  it('shows who made an entry, from where and in which session', async () => {
    setActivePinia(createPinia())
    const wrapper = mount(AuditView)
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0]!.text()).toContain('Anna Admin')
    expect(rows[0]!.text()).toContain('f3a1-subject')
    const context = rows[0]!.find('dl').text()
    for (const value of ['gateway-admin · IT', '192.0.2.10', 'a1b2c3', '4bf92f35', 'Mozilla/5.0']) expect(context).toContain(value)

    // Entries from before the upgrade have only the subject and details.
    expect(rows[1]!.text()).toContain('system:seed')
    expect(rows[1]!.find('dl').exists()).toBe(false)
  })
})
