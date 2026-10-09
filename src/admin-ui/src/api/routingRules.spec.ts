import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '.'

function stubFetch(body: unknown = '', status = 200) {
  document.cookie = 'XSRF-TOKEN=token; path=/'
  // A Response body can be read once, so every call gets its own.
  const fetch = vi
    .fn()
    .mockImplementation(() => Promise.resolve(new Response(body === '' ? null : JSON.stringify(body), { status: body === '' ? 204 : status, headers: { 'Content-Type': 'application/json' } })))
  vi.stubGlobal('fetch', fetch)
  return fetch
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/'
})

describe('deleting a team or department that has routing rules', () => {
  it('sends no query string unless the user has made a choice', async () => {
    const fetch = stubFetch()
    await api.teams.remove('t 1')
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/teams/t%201')
    expect(fetch.mock.calls[0]?.[1]).toMatchObject({ method: 'DELETE' })
  })

  it('sends the choice for a team and for a department', async () => {
    const fetch = stubFetch()
    await api.teams.remove('t1', 'deactivate')
    await api.departments.remove('d1', 'delete')
    expect(fetch.mock.calls.map((c) => c[0])).toEqual(['/api/teams/t1?routingRules=deactivate', '/api/departments/d1?routingRules=delete'])
  })
})

describe('routing rule endpoints', () => {
  it('lists with filters and omits empty ones', async () => {
    const fetch = stubFetch([])
    await api.routingRules.list({ scope: 'Team', scopeId: 't1', orphaned: true })
    await api.routingRules.list()
    expect(fetch.mock.calls.map((c) => c[0])).toEqual(['/api/routing-rules?scope=Team&scopeId=t1&orphaned=true', '/api/routing-rules'])
  })

  it('posts reassign, reorder, validate and test to their own paths', async () => {
    const fetch = stubFetch({})
    await api.routingRules.reassign('r/1', { scope: 'Team', scopeId: 't2', enable: true })
    await api.routingRules.reorder({ scope: 'Global', scopeId: null, ruleIds: ['a', 'b'] })
    await api.routingRules.validate('budget_used > 90')
    await api.routingRules.test({ model: 'ume/chat' })
    expect(fetch.mock.calls.map((c) => [c[1]?.method, c[0]])).toEqual([
      ['POST', '/api/routing-rules/r%2F1/reassign'],
      ['POST', '/api/routing-rules/reorder'],
      ['POST', '/api/routing-rules/validate'],
      ['POST', '/api/routing-rules/test'],
    ])
    expect(JSON.parse(fetch.mock.calls[2]?.[1]?.body as string)).toEqual({ condition: 'budget_used > 90' })
  })
})
