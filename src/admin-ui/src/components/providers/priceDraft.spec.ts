import { describe, expect, it } from 'vitest'
import { emptyPriceDraft, formatAudioPrice, isPriceDraftEmpty, toNewPrice, validatePrice } from './priceDraft'

describe('priceDraft per-image price', () => {
  const base = { ...emptyPriceDraft(), input: '0', cached: '0', output: '0' }

  it('sends the per-picture price, and 0 when it is left empty', () => {
    expect(toNewPrice({ ...base, image: '0,04' }).perImageUsd).toBe(0.04)
    expect(toNewPrice(base).perImageUsd).toBe(0)
  })

  it('counts a typed per-picture price as a filled-in draft', () => {
    expect(isPriceDraftEmpty(emptyPriceDraft())).toBe(true)
    expect(isPriceDraftEmpty({ ...emptyPriceDraft(), image: '0.04' })).toBe(false)
  })

  it('sends a cache-write price only when one is typed', () => {
    expect(toNewPrice({ ...base, cacheWrite: '3,75' }).cacheWriteInputPerMillionUsd).toBe(3.75)
    expect(toNewPrice(base).cacheWriteInputPerMillionUsd).toBeNull()
    expect(isPriceDraftEmpty({ ...emptyPriceDraft(), cacheWrite: '3.75' })).toBe(false)
    expect(validatePrice({ ...base, cacheWrite: '-1' }).cacheWrite).toBeTruthy()
    expect(validatePrice({ ...base, cacheWrite: '' }).cacheWrite).toBeUndefined()
  })

  it('stores an audio duration price typed per second or per hour as the price per minute', () => {
    expect(toNewPrice({ ...base, audio: '0,006' }).audioPerMinuteUsd).toBe(0.006)
    expect(toNewPrice({ ...base, audio: '0.0001', audioUnit: 'second' }).audioPerMinuteUsd).toBe(0.006)
    expect(toNewPrice({ ...base, audio: '0.36', audioUnit: 'hour' }).audioPerMinuteUsd).toBe(0.006)
    expect(toNewPrice({ ...base, audio: '0.40', audioUnit: 'hour' }).audioPerMinuteUsd).toBe(0.0066666667)
    expect(toNewPrice({ ...base, audioUnit: 'hour' }).audioPerMinuteUsd).toBe(0)
    expect(isPriceDraftEmpty({ ...emptyPriceDraft(), audioUnit: 'hour' })).toBe(true)
  })

  it('shows an audio duration price in the unit it reads best in', () => {
    expect(formatAudioPrice(0.006)).toBe('0,006 per min')
    expect(formatAudioPrice(0.0066666667)).toBe('0,40 per hour')
    expect(formatAudioPrice(0.0010002)).toBe('0,0010002 per min')
  })

  it('refuses a negative or non-numeric per-picture price but accepts an empty one', () => {
    expect(validatePrice(base)).toEqual({})
    expect(validatePrice({ ...base, image: '-1' }).image).toBeTruthy()
    expect(validatePrice({ ...base, image: 'abc' }).image).toBeTruthy()
    expect(validatePrice({ ...base, image: '0.04' }).image).toBeUndefined()
  })
})
