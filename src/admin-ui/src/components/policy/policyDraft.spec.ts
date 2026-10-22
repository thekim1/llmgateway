import { describe, expect, it } from 'vitest'
import { emptyPolicyDraft, fromPolicyDraft, toPolicyDraft } from './policyDraft'

describe('policyDraft', () => {
  it('sends only what is filled in, and {} to clear', () => {
    expect(fromPolicyDraft(emptyPolicyDraft()).policy).toEqual({})
    const { policy, errors } = fromPolicyDraft({ maxOutputTokens: '500', systemPrompt: ' Svara på svenska. ', defaults: '{"temperature": 0.2}', overrides: '' })
    expect(errors).toEqual({})
    expect(policy).toEqual({ maxOutputTokens: 500, systemPrompt: 'Svara på svenska.', defaults: { temperature: 0.2 } })
  })

  it('round-trips a stored policy', () => {
    const stored = { maxOutputTokens: 100, defaults: { top_p: 0.9 }, overrides: { user: 'x' } }
    expect(fromPolicyDraft(toPolicyDraft(stored)).policy).toEqual(stored)
  })

  it('refuses bad numbers, invalid JSON and protected parameters', () => {
    const { errors } = fromPolicyDraft({ maxOutputTokens: '0', systemPrompt: '', defaults: '[1]', overrides: '{"model": "gpt"}' })
    expect(errors.maxOutputTokens).toBeTruthy()
    expect(errors.defaults).toMatch(/JSON object/)
    expect(errors.overrides).toMatch(/'model'/)
    expect(fromPolicyDraft({ ...emptyPolicyDraft(), overrides: '{"max_tokens": 5}' }).errors.overrides).toMatch(/Max output tokens/)
  })
})
