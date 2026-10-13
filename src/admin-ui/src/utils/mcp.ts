/** Client-side checks for MCP server settings; they mirror the admin API's rules (docs/admin-api.md, MCP servers). */

export const MAX_SLUG_LENGTH = 24
const SLUG = /^[a-z][a-z0-9]*(_[a-z0-9]+)*$/
const HEADER_NAME = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/

/** Headers the gateway never forwards from callers, whatever a server's allow-list says. */
const NEVER_FORWARDED = new Set([
  'authorization', 'proxy-authorization', 'x-api-key', 'api-key', 'cookie', 'host', 'content-type', 'content-length',
  'accept', 'connection', 'transfer-encoding', 'mcp-session-id', 'mcp-protocol-version', 'mcp-method', 'mcp-name',
])

/** Headers a static credential may not set: they belong to HTTP or the MCP transport. */
const RESERVED_STATIC = new Set([
  'host', 'content-type', 'content-length', 'transfer-encoding', 'connection', 'keep-alive', 'proxy-connection', 'te', 'trailer',
  'upgrade', 'accept', 'last-event-id', 'mcp-session-id', 'mcp-protocol-version', 'mcp-method', 'mcp-name',
])

export function slugProblem(slug: string): string | null {
  if (!slug) return 'Enter a short name.'
  if (slug.length > MAX_SLUG_LENGTH) return `Use at most ${MAX_SLUG_LENGTH} characters.`
  if (!SLUG.test(slug)) return 'Use lower-case letters and digits, starting with a letter, with single underscores between words (no double or trailing underscore).'
  return null
}

/** One header name per line or comma. */
export function parseHeaderList(text: string): string[] {
  return text.split(/[\n,]/).map((h) => h.trim()).filter((h) => h !== '')
}

export function forwardHeadersProblem(headers: string[]): string | null {
  const bad = headers.find((h) => h.length > 100 || !HEADER_NAME.test(h))
  if (bad) return `“${bad}” is not a valid header name.`
  const never = headers.find((h) => NEVER_FORWARDED.has(h.toLowerCase()))
  if (never) return `${never} is never forwarded: credentials, cookies and protocol headers stay with the gateway.`
  if (new Set(headers.map((h) => h.toLowerCase())).size !== headers.length) return 'List each header once.'
  if (headers.length > 20) return 'List at most 20 headers.'
  return null
}

export function staticHeadersProblem(headers: { name: string; value: string }[]): string | null {
  if (headers.length === 0) return 'Add at least one header.'
  if (headers.length > 20) return 'Use at most 20 headers.'
  const badName = headers.find((h) => !HEADER_NAME.test(h.name.trim()))
  if (badName) return badName.name.trim() ? `“${badName.name.trim()}” is not a valid header name.` : 'Every header needs a name.'
  const reserved = headers.find((h) => RESERVED_STATIC.has(h.name.trim().toLowerCase()))
  if (reserved) return `${reserved.name.trim()} belongs to HTTP or MCP and can’t be set.`
  if (headers.some((h) => !h.value || /[\r\n]/.test(h.value))) return 'Every header needs a value without line breaks.'
  if (new Set(headers.map((h) => h.name.trim().toLowerCase())).size !== headers.length) return 'Use each header name once.'
  return null
}

/** A JSON value for display: strings as they are, everything else as indented JSON. */
export function jsonText(value: unknown): string {
  if (value === undefined) return '—'
  return typeof value === 'string' ? value : JSON.stringify(value, null, 2)
}
