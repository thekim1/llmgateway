import { describe, expect, it } from 'vitest'
import { addressListError, formatAddressList, MAX_ADDRESS_ENTRIES, parseAddressList } from './addresses'

describe('parseAddressList', () => {
  it('accepts one entry per line, commas and stray spaces', () => {
    expect(parseAddressList(' 10.0.0.0/24\n\n2001:db8::1, 192.0.2.7 ')).toEqual(['10.0.0.0/24', '2001:db8::1', '192.0.2.7'])
    expect(formatAddressList(['10.0.0.0/24', '192.0.2.7'])).toBe('10.0.0.0/24\n192.0.2.7')
  })
})

describe('addressListError', () => {
  it('accepts addresses and networks of both families', () => {
    expect(addressListError(['192.0.2.10', '10.20.0.0/16', '2001:db8::1', '2001:db8::/32', '::ffff:192.0.2.1'])).toBeNull()
  })

  it.each(['10', '10.1', '256.0.0.1', 'proxy.example.se', '10.0.0.0/33', '2001:db8::/129', 'fe80::1%eth0', '10.0.0.0/8/8', '10.0.0.0/x'])(
    'rejects %s',
    (entry) => {
      expect(addressListError([entry])).toMatch(/not an IP address|invalid network size/)
    },
  )

  it('suggests the network address when host bits are set', () => {
    expect(addressListError(['10.20.1.5/16'])).toContain('10.20.0.0/16')
  })

  it('caps the number of entries', () => {
    expect(addressListError(Array.from({ length: MAX_ADDRESS_ENTRIES + 1 }, (_, i) => `10.0.0.${i % 256}`))).toContain('at most')
  })
})
