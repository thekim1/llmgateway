import type { LocationQuery, LocationQueryRaw, RouteLocationRaw } from 'vue-router'
import { REQUEST_OUTCOMES, type RequestOutcome } from '@/api/types'

/** `errors` is every request that did not succeed; an outcome narrows it to one kind. */
export type RequestsOutcomeFilter = RequestOutcome | 'errors'

/** Filters for the request list on Usage › Requests and costs, as other pages link to it. Dates are `yyyy-mm-dd`. */
export interface RequestsLink {
  from?: string
  to?: string
  departmentId?: string
  teamId?: string
  keyId?: string
  model?: string
  provider?: string
  outcome?: RequestsOutcomeFilter
}

export const REQUESTS_LINK_KEYS = ['from', 'to', 'departmentId', 'teamId', 'keyId', 'model', 'provider', 'outcome'] as const

const DATE = /^\d{4}-\d{2}-\d{2}$/

/** A link to the request list with these filters, e.g. the failed requests of the last day. */
export function requestsLink(filter: RequestsLink): RouteLocationRaw {
  const query: LocationQueryRaw = { tab: 'requests' }
  for (const key of REQUESTS_LINK_KEYS) if (filter[key]) query[key] = filter[key]
  return { name: 'usage', query }
}

/** The filters in a request-list link; null when the query has none. Values that do not parse are ignored. */
export function readRequestsLink(query: LocationQuery): RequestsLink | null {
  const text = (key: string) => (typeof query[key] === 'string' && query[key] ? (query[key] as string) : undefined)
  const outcome = text('outcome')
  const link: RequestsLink = {
    from: DATE.test(text('from') ?? '') ? text('from') : undefined,
    to: DATE.test(text('to') ?? '') ? text('to') : undefined,
    departmentId: text('departmentId'),
    teamId: text('teamId'),
    keyId: text('keyId'),
    model: text('model'),
    provider: text('provider'),
    outcome: outcome === 'errors' || (REQUEST_OUTCOMES as readonly string[]).includes(outcome ?? '') ? (outcome as RequestsOutcomeFilter) : undefined,
  }
  return Object.values(link).some((v) => v !== undefined) ? link : null
}

/** The query without the request-list filters, so following a link once does not leave them in the address. */
export function withoutRequestsLink(query: LocationQuery): LocationQueryRaw {
  const rest: LocationQueryRaw = { ...query }
  for (const key of REQUESTS_LINK_KEYS) delete rest[key]
  return rest
}
