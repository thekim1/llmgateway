import { describe, expect, it } from 'vitest'
import { effectiveRoles } from '@/stores/auth'
import { routes } from '.'
import { GATEWAY_ADMIN, navGroups } from './nav'

describe('routing rules navigation', () => {
  const item = navGroups.flatMap((g) => g.items).find((i) => i.name === 'routing-rules')

  it('is in the Routing group next to Routes and Providers', () => {
    const group = navGroups.find((g) => g.items.some((i) => i.name === 'routing-rules'))
    expect(group?.label).toBe('Routing')
    expect(group?.items.map((i) => i.name)).toEqual(['routes', 'routing-rules', 'providers'])
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
