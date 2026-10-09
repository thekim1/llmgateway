import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { rule } from '@/test/fixtures'
import { useRoutingRulesStore } from './routingRules'

const list = vi.fn()
const create = vi.fn()
const update = vi.fn()
const remove = vi.fn()
const reassign = vi.fn()
const reorder = vi.fn()
vi.mock('@/api', () => ({
  api: {
    routingRules: {
      list: (...a: unknown[]) => list(...a),
      create: (...a: unknown[]) => create(...a),
      update: (...a: unknown[]) => update(...a),
      remove: (...a: unknown[]) => remove(...a),
      reassign: (...a: unknown[]) => reassign(...a),
      reorder: (...a: unknown[]) => reorder(...a),
    },
  },
}))

describe('routing rules store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    for (const fn of [list, create, update, remove, reassign, reorder]) fn.mockReset()
  })

  it('loads the rules', async () => {
    const a = rule()
    list.mockResolvedValue([a])
    const store = useRoutingRulesStore()
    await store.load()
    expect(store.items).toEqual([a])
    expect(store.loaded).toBe(true)
  })

  it('keeps the error when loading fails', async () => {
    list.mockRejectedValue(new Error('down'))
    const store = useRoutingRulesStore()
    await store.load()
    expect(store.error).toBeInstanceOf(Error)
    expect(store.items).toEqual([])
  })

  it('adds, replaces and removes rules as the API confirms them', async () => {
    const a = rule()
    const store = useRoutingRulesStore()
    create.mockResolvedValue(a)
    await store.create({ name: a.name, scope: 'Global', isEnabled: true, priority: 0, chain: false, targets: a.targets, fallbacks: [] })
    expect(store.items).toEqual([a])
    update.mockResolvedValue({ ...a, name: 'Renamed' })
    await store.update(a.id, { name: 'Renamed', scope: 'Global', isEnabled: true, priority: 0, chain: false, targets: a.targets, fallbacks: [] })
    expect(store.items.map((r) => r.name)).toEqual(['Renamed'])
    await store.remove(a.id)
    expect(remove).toHaveBeenCalledWith(a.id)
    expect(store.items).toEqual([])
  })

  it('switches a rule on or off by saving it unchanged apart from isEnabled', async () => {
    const a = rule({ condition: 'budget_used > 90', fallbacks: ['x'], priority: 30 })
    const store = useRoutingRulesStore()
    store.items = [a]
    update.mockResolvedValue({ ...a, isEnabled: false })
    await store.setEnabled(a, false)
    expect(update).toHaveBeenCalledWith(a.id, expect.objectContaining({ isEnabled: false, condition: 'budget_used > 90', fallbacks: ['x'], priority: 30, name: a.name }))
    expect(store.items[0]!.isEnabled).toBe(false)
  })

  it('takes the new priorities from the server when reordering', async () => {
    const a = rule({ priority: 0 })
    const b = rule({ priority: 10 })
    const store = useRoutingRulesStore()
    store.items = [a, b]
    reorder.mockResolvedValue([{ ...b, priority: 0 }, { ...a, priority: 10 }])
    await store.reorder('Global', null, [b.id, a.id])
    expect(reorder).toHaveBeenCalledWith({ scope: 'Global', scopeId: null, ruleIds: [b.id, a.id] })
    expect(store.items.map((r) => [r.id, r.priority])).toEqual([[a.id, 10], [b.id, 0]])
  })

  it('shows a reassigned rule as owned and enabled', async () => {
    const orphan = rule({ scope: 'Team', scopeId: 'gone', isOrphaned: true, isEnabled: false })
    const store = useRoutingRulesStore()
    store.items = [orphan]
    reassign.mockResolvedValue({ ...orphan, scopeId: 't2', scopeName: 'New team', isOrphaned: false, isEnabled: true })
    await store.reassign(orphan.id, { scope: 'Team', scopeId: 't2', enable: true })
    expect(store.items[0]).toMatchObject({ scopeName: 'New team', isOrphaned: false, isEnabled: true })
  })
})
