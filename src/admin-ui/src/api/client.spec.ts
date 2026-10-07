import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, buildUrl, request, postLogout } from './client'

afterEach(() => { vi.unstubAllGlobals(); document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/' })
describe('metadata-only BFF client', () => {
  it('rejects malformed JSON instead of returning a success-shaped value', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{invalid', { headers: { 'Content-Type': 'application/json' } })))
    await expect(request('GET', '/api/teams')).rejects.toMatchObject({ status: 502 })
  })
  it('surfaces a missing anti-forgery cookie before sending a mutation', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('{}', { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetch)
    await expect(request('POST', '/api/teams', { body: { name: 'Test' } })).rejects.toMatchObject({ status: 400 })
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch.mock.calls[0]?.[0]).toBe('/bff/user')
  })
  it('encodes filters and omits empty values', () => {
    expect(buildUrl('/api/keys', { status: 'Active', teamId: 'a/b', empty: '', zero: 0, absent: undefined })).toBe('/api/keys?status=Active&teamId=a%2Fb&zero=0')
  })
  it('sends anti-forgery headers and same-origin credentials', async () => {
    document.cookie = 'XSRF-TOKEN=test-token; path=/'
    const fetch = vi.fn().mockResolvedValue(new Response('{"id":"1"}', { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetch)
    await request('POST', '/api/teams', { body: { name: 'Team' } })
    expect(fetch).toHaveBeenCalledWith('/api/teams', expect.objectContaining({ credentials: 'same-origin', headers: expect.objectContaining({ 'X-XSRF-TOKEN': 'test-token', 'X-Requested-With': 'XMLHttpRequest' }) }))
  })
  it('surfaces field validation without logging request bodies', async () => {
    document.cookie = 'XSRF-TOKEN=token; path=/'
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"detail":"Invalid name","errors":{"name":["Required"]}}', { status: 400, headers: { 'Content-Type': 'application/problem+json' } })))
    await expect(request('POST', '/api/keys', { body: { name: '' } })).rejects.toMatchObject({ status: 400, fieldErrors: { name: ['Required'] } })
  })
  it('does not disguise a failed logout as success', async () => {
    document.cookie = 'XSRF-TOKEN=token; path=/'
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"detail":"CSRF invalid"}', { status: 400, headers: { 'Content-Type': 'application/problem+json' } })))
    await expect(postLogout()).rejects.toBeInstanceOf(ApiError)
  })
  it('returns the server-generated OIDC signout redirect', async () => {
    document.cookie = 'XSRF-TOKEN=token; path=/'
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"redirectUrl":"https://identity.example/logout"}', { headers: { 'Content-Type': 'application/json' } })))
    expect(await postLogout()).toBe('https://identity.example/logout')
  })
})
