/**
 * IP address lists (a key's allowed client addresses, the gateway's trusted proxies): one entry per line, a single
 * address (192.0.2.10, 2001:db8::1) or a CIDR network (10.20.0.0/16). The admin API is the authority and returns the
 * entries in canonical form; this only catches typos before the round trip.
 */

/** Most entries a list may hold (NetworkRanges.MaxEntries in the backend). */
export const MAX_ADDRESS_ENTRIES = 100

/** Splits on new lines, commas and spaces; blank entries are dropped. */
export function parseAddressList(text: string): string[] {
  return text
    .split(/[\s,]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '')
}

export function formatAddressList(entries: readonly string[] | null | undefined): string {
  return (entries ?? []).join('\n')
}

function ipv4Octets(text: string): number[] | null {
  const parts = text.split('.')
  if (parts.length !== 4 || parts.some((p) => !/^\d{1,3}$/.test(p))) return null
  const octets = parts.map(Number)
  return octets.every((o) => o <= 255) ? octets : null
}

function isIpv6(text: string): boolean {
  if (!text.includes(':') || text.includes('%') || !/^[0-9a-fA-F:.]+$/.test(text)) return false
  try {
    // The URL parser implements the full IPv6 grammar, including embedded IPv4.
    return new URL(`http://[${text}]/`).hostname !== ''
  } catch {
    return false
  }
}

/** An English message for the first invalid entry, or null when every entry looks valid. */
export function addressListError(entries: readonly string[]): string | null {
  if (entries.length > MAX_ADDRESS_ENTRIES) return `Enter at most ${MAX_ADDRESS_ENTRIES} addresses or networks.`
  for (const entry of entries) {
    const [address = '', prefix, extra] = entry.split('/')
    if (extra !== undefined) return `“${entry}” is not an IP address or network.`
    const octets = ipv4Octets(address)
    if (!octets && !isIpv6(address)) return `“${entry}” is not an IP address or network. Use for example 192.0.2.10 or 10.20.0.0/16.`
    if (prefix === undefined) continue
    const max = octets ? 32 : 128
    if (!/^\d{1,3}$/.test(prefix) || Number(prefix) > max) return `“${entry}” has an invalid network size; use /0 to /${max}.`
    if (octets) {
      const bits = Number(prefix)
      const value = octets.reduce((acc, o) => acc * 256 + o, 0)
      const size = 2 ** (32 - bits)
      const network = value - (value % size)
      if (network !== value) {
        const meant = [24, 16, 8, 0].map((shift) => Math.floor(network / 2 ** shift) % 256).join('.')
        return `“${entry}” is not a network address. Did you mean ${meant}/${bits}?`
      }
    }
  }
  return null
}
