import { describe, expect, it } from 'vitest'
import { effectiveRoles } from '@/stores/auth'
import { routes } from '.'
import { GATEWAY_ADMIN, filterEntries, isNavItemActive, mobileTabs, navGroups, searchEntries } from './nav'

describe('routing rules navigation', () => {
  const item = navGroups.flatMap((g) => g.items).find((i) => i.name === 'routing-rules')

  it('is in the Models & routing group next to Routes and Providers', () => {
    const group = navGroups.find((g) => g.items.some((i) => i.name === 'routing-rules'))
    expect(group?.label).toBe('Models & routing')
    expect(group?.items.map((i) => i.name)).toEqual(['catalog', 'providers', 'routes', 'routing-rules'])
  })

  it('is only for gateway administrators, in the menu and as a page', () => {
    expect(item?.roles).toEqual(GATEWAY_ADMIN)
    const route = routes.find((r) => r.name === 'routing-rules')
    expect(route?.path).toBe('/routing-rules')
    expect(route?.meta?.roles).toEqual(GATEWAY_ADMIN)
    expect(route?.meta?.title).toBe('Routing rules')
    const allowed = (roles: string[]) => GATEWAY_ADMIN.some((r) => effectiveRoles(roles).has(r))
    expect(allowed(['gateway-admin'])).toBe(true)
    expect(allowed(['department-admin'])).toBe(false)
    expect(allowed(['viewer'])).toBe(false)
  })

  it('has an icon', () => {
    expect(item?.icon).toBe('rule')
  })
})

describe('menu structure', () => {
  const items = navGroups.flatMap((g) => g.items)

  it('lists every page once, and every menu page exists as a route', () => {
    const names = items.map((i) => i.name)
    expect(new Set(names).size).toBe(names.length)
    for (const name of names) expect(routes.some((r) => r.name === name)).toBe(true)
  })

  it('uses the page title as the menu label, so the menu and the page agree', () => {
    for (const item of items) {
      const title = routes.find((r) => r.name === item.name)?.meta?.title
      expect(item.label).toBe(title)
    }
  })

  it('keeps settings out of Cost & compliance and the catalogue out of Access', () => {
    const groupOf = (name: string) => navGroups.find((g) => g.items.some((i) => i.name === name))?.id
    expect(groupOf('settings')).toBe('system')
    expect(groupOf('catalog')).toBe('models')
    expect(groupOf('pii-incidents')).toBe('cost')
  })

  it('keeps Health highlighted on its provider stability sub-page', () => {
    const ops = items.find((i) => i.name === 'ops')!
    expect(isNavItemActive(ops, 'provider-stability')).toBe(true)
    expect(isNavItemActive(mobileTabs.find((t) => t.name === 'ops')!, 'provider-stability')).toBe(true)
    expect(isNavItemActive(ops, 'usage')).toBe(false)
  })
})

describe('search', () => {
  const entries = searchEntries(navGroups)
  const labels = (q: string) => filterEntries(entries, q).map((e) => (e.parent ? `${e.parent} › ${e.label}` : e.label))

  it('lists only pages, in menu order, without a query', () => {
    expect(labels('')).toEqual(navGroups.flatMap((g) => g.items.map((i) => i.label)))
  })

  it('finds pages by words people use that are not in the label', () => {
    expect(labels('circuit')[0]).toBe('Health & operations')
    expect(labels('department')).toContain('Organisation')
    expect(labels('gdpr')).toContain('PII incidents')
  })

  it('finds sections inside a page', () => {
    expect(labels('retention')[0]).toBe('Settings › Data retention')
    expect(labels('exchange')[0]).toBe('Settings › Exchange rate')
    expect(labels('stability')[0]).toBe('Health & operations › Provider stability')
  })

  it('needs every word to match', () => {
    expect(labels('providers models')).toEqual(['Providers & models', 'Providers & models › Providers', 'Providers & models › Models'])
    expect(labels('catalogue providers')).toEqual([])
  })
})
