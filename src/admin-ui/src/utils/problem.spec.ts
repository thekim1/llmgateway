import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { mapFieldErrors, scopedRulesProblem } from './problem'

const conflict = (problem: Record<string, unknown>) => new ApiError(409, 'DELETE', problem)

describe('scopedRulesProblem', () => {
  it('recognises the answer to deleting a team or department that has routing rules', () => {
    const problem = { code: 'routing_rules_scoped', choices: ['delete', 'deactivate'], rules: [{ id: 'r1', name: 'Premium', isEnabled: true }], detail: 'The team is the only owner of 1 routing rule(s).' }
    expect(scopedRulesProblem(conflict(problem))?.rules).toEqual([{ id: 'r1', name: 'Premium', isEnabled: true }])
  })

  it('is null for every other error', () => {
    expect(scopedRulesProblem(conflict({ detail: 'The team has keys and cannot be deleted.' }))).toBeNull()
    expect(scopedRulesProblem(new ApiError(400, 'DELETE', { code: 'routing_rules_scoped', rules: [] }))).toBeNull()
    expect(scopedRulesProblem(conflict({ code: 'routing_rules_scoped' }))).toBeNull() // no list to show
    expect(scopedRulesProblem(new Error('boom'))).toBeNull()
    expect(scopedRulesProblem(null)).toBeNull()
  })
})

describe('mapFieldErrors', () => {
  it('maps nested item errors such as Targets[0].weight onto the targets field', () => {
    const error = new ApiError(400, 'POST', { errors: { 'Targets[0].weight': ['The value is invalid.'], condition: ['Unknown variable (character 3)'] } })
    expect(mapFieldErrors(error, ['name', 'targets', 'condition'])).toEqual({ targets: 'The value is invalid.', condition: 'Unknown variable (character 3)' })
  })

  it('still matches plain and $.-prefixed names, ignoring case', () => {
    const error = new ApiError(400, 'POST', { errors: { Name: ['x'], '$.scopeId': ['y'] } })
    expect(mapFieldErrors(error, ['name', 'scopeId'])).toEqual({ name: 'x', scopeId: 'y' })
  })
})
