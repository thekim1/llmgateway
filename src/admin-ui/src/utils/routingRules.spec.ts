import { describe, expect, it } from 'vitest'
import { rule } from '@/test/fixtures'
import { groupRules, isInEffect, targetShares, toRequest } from './routingRules'

describe('groupRules', () => {
  it('lists scopes in the order the gateway checks them, and rules by priority then name', () => {
    const sections = groupRules([
      rule({ name: 'Global b', priority: 5 }),
      rule({ name: 'Global a', priority: 5 }),
      rule({ name: 'Global first', priority: 1 }),
      rule({ name: 'Key rule', scope: 'VirtualKey', scopeId: 'k1', scopeName: 'CRM key' }),
      rule({ name: 'Team rule', scope: 'Team', scopeId: 't1', scopeName: 'Service desk' }),
      rule({ name: 'Department rule', scope: 'Department', scopeId: 'd1', scopeName: 'Kommunstyrelsen' }),
    ])
    expect(sections.map((s) => s.scope)).toEqual(['VirtualKey', 'Team', 'Department', 'Global'])
    expect(sections[3]!.groups[0]!.rules.map((r) => r.name)).toEqual(['Global first', 'Global a', 'Global b'])
  })

  it('keeps rules of different owners apart, because only an owner’s own rules compete', () => {
    const [team] = groupRules([
      rule({ name: 'B1', scope: 'Team', scopeId: 'b', scopeName: 'Beta', priority: 0 }),
      rule({ name: 'A1', scope: 'Team', scopeId: 'a', scopeName: 'Alfa', priority: 9 }),
      rule({ name: 'A0', scope: 'Team', scopeId: 'a', scopeName: 'Alfa', priority: 1 }),
    ])
    expect(team!.groups.map((g) => [g.ownerName, g.rules.map((r) => r.name)])).toEqual([
      ['Alfa', ['A0', 'A1']],
      ['Beta', ['B1']],
    ])
  })

  it('puts rules without an owner after the owned ones in their scope', () => {
    const [team] = groupRules([
      rule({ name: 'Lost', scope: 'Team', scopeId: 'gone', scopeName: null, isOrphaned: true, isEnabled: false }),
      rule({ name: 'Owned', scope: 'Team', scopeId: 'z', scopeName: 'Zeta' }),
    ])
    expect(team!.groups.map((g) => g.orphaned)).toEqual([false, true])
  })

  it('omits scopes without rules', () => {
    expect(groupRules([])).toEqual([])
    expect(groupRules([rule({ scope: 'Global' })]).map((s) => s.scope)).toEqual(['Global'])
  })
})

describe('targetShares', () => {
  it('turns weights into whole percentages that add up to 100', () => {
    expect(targetShares([70, 30])).toEqual([70, 30])
    expect(targetShares([1, 1, 1]).reduce((a, b) => a + b, 0)).toBe(100)
    expect(targetShares([1, 1, 1])).toEqual([34, 33, 33])
    expect(targetShares([1])).toEqual([100])
  })

  it('copes with zero and invalid weights', () => {
    expect(targetShares([0, 0])).toEqual([0, 0])
    expect(targetShares([])).toEqual([])
    expect(targetShares([-5, 5])).toEqual([0, 100])
  })
})

describe('toRequest', () => {
  it('re-saves a rule unchanged, with optional overrides, without sharing arrays with the original', () => {
    const original = rule({ targets: [{ model: 'a', weight: 2 }], fallbacks: ['b'], condition: 'x' })
    const request = toRequest(original, { isEnabled: false })
    expect(request).toMatchObject({ name: original.name, scope: 'Global', scopeId: null, isEnabled: false, priority: original.priority, condition: 'x', chain: false, fallbacks: ['b'] })
    request.targets[0]!.weight = 9
    request.fallbacks.push('c')
    expect(original.targets[0]!.weight).toBe(2)
    expect(original.fallbacks).toEqual(['b'])
  })
})

describe('isInEffect', () => {
  it('needs the rule enabled, owned and accepted by the gateway', () => {
    expect(isInEffect(rule())).toBe(true)
    expect(isInEffect(rule({ isEnabled: false }))).toBe(false)
    expect(isInEffect(rule({ isOrphaned: true }))).toBe(false)
    expect(isInEffect(rule({ validationErrors: [{ message: 'x', position: null, length: null }] }))).toBe(false)
  })
})
