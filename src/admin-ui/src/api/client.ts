import type { ProblemDetails } from './types'

export const XSRF_COOKIE = 'XSRF-TOKEN'
export const XSRF_HEADER = 'X-XSRF-TOKEN'

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE'
export type QueryValue = string | number | boolean | null | undefined
export type Query = Record<string, QueryValue>

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null
  readonly method: HttpMethod

  constructor(status: number, method: HttpMethod, problem: ProblemDetails | null, message?: string) {
    super(message ?? problem?.detail ?? problem?.title ?? `HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.method = method
    this.problem = problem
  }

  get fieldErrors(): Record<string, string[]> {
    return this.problem?.errors ?? {}
  }

  get isNetworkError(): boolean {
    return this.status === 0
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError
}

export interface ClientHooks {
  /** Called on any 401 – the session has expired or the user is not signed in. */
  onUnauthorized?: () => void
  /** Called on 403. */
  onForbidden?: (method: HttpMethod, path: string) => void
}

let hooks: ClientHooks = {}

export function configureClient(next: ClientHooks): void {
  hooks = { ...hooks, ...next }
}

export function readCookie(name: string): string | null {
  if (typeof document === 'undefined') return null
  const prefix = `${name}=`
  for (const part of document.cookie.split(';')) {
    const trimmed = part.trim()
    if (trimmed.startsWith(prefix)) return decodeURIComponent(trimmed.slice(prefix.length))
  }
  return null
}

export function buildUrl(path: string, query?: Query): string {
  if (!query) return path
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') continue
    params.append(key, String(value))
  }
  const qs = params.toString()
  return qs ? `${path}${path.includes('?') ? '&' : '?'}${qs}` : path
}

/** The XSRF cookie is set by GET /bff/user. If it is missing (e.g. expired), fetch it once. */
async function getCsrfToken(): Promise<string> {
  let token = readCookie(XSRF_COOKIE)
  if (!token) {
    try {
      const response = await fetch('/bff/user', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      if (!response.ok) throw new ApiError(response.status, 'GET', null)
    } catch (error) {
      if (isApiError(error)) throw error
      throw new ApiError(0, 'GET', null, 'network')
    }
    token = readCookie(XSRF_COOKIE)
  }
  if (!token) throw new ApiError(400, 'GET', null, 'CSRF cookie missing')
  return token
}

async function parseBody(response: Response, method: HttpMethod): Promise<unknown> {
  if (response.status === 204 || response.status === 205) return undefined
  const contentType = response.headers.get('content-type') ?? ''
  const text = await response.text()
  if (!text) return undefined
  if (contentType.includes('json')) {
    try {
      return JSON.parse(text)
    } catch {
      throw new ApiError(response.ok ? 502 : response.status, method, null, 'Invalid JSON response')
    }
  }
  return text
}

export interface RequestOptions {
  query?: Query
  body?: unknown
  signal?: AbortSignal
}

export async function request<T>(method: HttpMethod, path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json, application/problem+json' }
  if (method !== 'GET') {
    headers['X-Requested-With'] = 'XMLHttpRequest'
    const token = await getCsrfToken()
    if (token) headers[XSRF_HEADER] = token
  }
  let body: BodyInit | undefined
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(options.body)
  }

  let response: Response
  try {
    response = await fetch(buildUrl(path, options.query), {
      method,
      headers,
      body,
      credentials: 'same-origin',
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, method, null, 'network')
  }

  const payload = await parseBody(response, method)

  if (!response.ok) {
    const problem: ProblemDetails | null =
      payload && typeof payload === 'object' ? (payload as ProblemDetails) : null
    if (response.status === 401) hooks.onUnauthorized?.()
    if (response.status === 403) hooks.onForbidden?.(method, path)
    throw new ApiError(response.status, method, problem)
  }

  return payload as T
}

export const http = {
  get: <T>(path: string, query?: Query, signal?: AbortSignal) => request<T>('GET', path, { query, signal }),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, { body }),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, { body }),
  del: <T = void>(path: string) => request<T>('DELETE', path),
}

/**
 * POST /bff/logout. The BFF may answer with a redirect to the identity provider's end-session
 * endpoint; the caller navigates to the URL returned in the JSON response.
 */
export async function postLogout(): Promise<string> {
  const token = await getCsrfToken()
  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-Requested-With': 'XMLHttpRequest',
  }
  if (token) headers[XSRF_HEADER] = token
  let response: Response
  try {
    response = await fetch('/bff/logout', {
      method: 'POST',
      headers,
      credentials: 'same-origin',
      redirect: 'manual',
    })
  } catch {
    throw new ApiError(0, 'POST', null, 'network')
  }
  const payload = await parseBody(response, 'POST')
  if (!response.ok) throw new ApiError(response.status, 'POST', payload && typeof payload === 'object' ? payload as ProblemDetails : null)
  if (payload && typeof payload === 'object' && 'redirectUrl' in payload && typeof payload.redirectUrl === 'string') return payload.redirectUrl
  throw new ApiError(502, 'POST', null, 'Invalid logout response')
}
