import { describe, expect, it } from 'vitest'
import { readRequestsLink, requestsLink, withoutRequestsLink } from './requestsLink'

describe('requestsLink', () => {
  it('opens the requests tab with only the filters that are set', () => {
    expect(requestsLink({ from: '2026-10-24', to: '2026-10-25', model: '', outcome: 'errors' })).toEqual({
      name: 'usage',
      query: { tab: 'requests', from: '2026-10-24', to: '2026-10-25', outcome: 'errors' },
    })
  })

  it('reads the filters back and ignores values that do not parse', () => {
    expect(readRequestsLink({ tab: 'requests', outcome: 'ProviderError', provider: '-', from: 'yesterday' })).toEqual({
      from: undefined,
      to: undefined,
      departmentId: undefined,
      teamId: undefined,
      keyId: undefined,
      model: undefined,
      provider: '-',
      outcome: 'ProviderError',
    })
    expect(readRequestsLink({ tab: 'requests', outcome: 'Exploded' })).toBeNull()
    expect(readRequestsLink({ tab: 'requests', request: 'req_1' })).toBeNull()
  })

  it('drops the filters from the address but keeps the rest', () => {
    expect(withoutRequestsLink({ tab: 'requests', outcome: 'errors', keyId: 'k', request: 'req_1' })).toEqual({ tab: 'requests', request: 'req_1' })
  })
})
