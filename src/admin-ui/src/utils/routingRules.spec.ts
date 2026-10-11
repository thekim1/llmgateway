import { describe, expect, it } from 'vitest'
import { rule } from '@/test/fixtures'
import { groupRules, isInEffect, ownerSummary, targetShares, toRequest } from './routingRules'

describe('groupRules', () => {
  it('lists scopes in the order the gateway checks them, and rules by priority then name', () => {
    const sections = groupRules([
      rule({ name: 'Global b', priority: 5 }),
      rule({ name: 'Global a', priority: 5 }),
      rule({ name: 'Global first', priority: 1 }),
      rule({ name: 'Key rule', scope: 'VirtualKey', scopeIds: ['k1'], owners: [{ id: 'k1', name: 'CRM key' }] }),
      rule({ name: 'Team rule', scope: 'Team', scopeIds: ['t1'], owners: [{ id: 't1', name: 'Service desk' }] }),
      rule({ name: 'Department rule', scope: 'Department', scopeIds: ['d1'], owners: [{ id: 'd1', name: 'Kommunstyrelsen' }] }),
    ])
    expect(sections.map((s) => s.scope)).toEqual(['VirtualKey', 'Team', 'Department', 'Global'])
    expect(sections[3]!.rules.map((r) => r.name)).toEqual(['Global first', 'Global a', 'Global b'])
  })

  it('orders a scope as one list, because a rule can be shared by several owners', () => {
    const [team] = groupRules([
      rule({ name: 'B1', scope: 'Team', scopeIds: ['b'], priority: 0 }),
      rule({ name: 'Shared', scope: 'Team', scopeIds: ['a', 'b'], priority: 5 }),
      rule({ name: 'A0', scope: 'Team', scopeIds: ['a'], priority: 1 }),
    ])
    expect(team!.rules.map((r) => r.name)).toEqual(['B1', 'A0', 'Shared'])
  })

  it('omits scopes without rules', () => {
    expect(groupRules([])).toEqual([])
    expect(groupRules([rule({ scope: 'Global' })]).map((s) => s.scope)).toEqual(['Global'])
  })
})

describe('ownerSummary', () => {
  it('names a few owners and counts the rest', () => {
    const owners = [
      { id: 'a', name: 'Alfa' },
      { id: 'b', name: 'Beta' },
      { id: 'c', name: 'Gamma' },
      { id: 'd', name: null },
    ]
    expect(ownerSummary(rule({ scope: 'Team', scopeIds: ['a', 'b', 'c', 'd'], owners }))).toBe('Alfa, Beta +1')
    expect(ownerSummary(rule({ scope: 'Team', scopeIds: ['d'], owners: [{ id: 'd', name: null }] }))).toBe('No owner')
    expect(ownerSummary(rule())).toBe('Every request')
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
    expect(request).toMatchObject({ name: original.name, scope: 'Global', scopeIds: [], isEnabled: false, priority: original.priority, condition: 'x', chain: false, fallbacks: ['b'] })
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
