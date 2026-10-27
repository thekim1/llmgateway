import type { NewPrice } from '@/api/types'
import { formatUsd } from '@/utils/format'

/** The unit an audio duration price is typed in. The API stores it per minute. */
export type AudioUnit = 'second' | 'minute' | 'hour'

const MINUTES: Record<AudioUnit, number> = { second: 1 / 60, minute: 1, hour: 60 }
export const AUDIO_UNIT_LABEL: Record<AudioUnit, string> = { second: 'per second', minute: 'per minute', hour: 'per hour' }
const AUDIO_UNIT_SHORT: Record<AudioUnit, string> = { second: 'per s', minute: 'per min', hour: 'per hour' }

export interface PriceDraft {
  input: string
  cached: string
  /** USD per 1M input tokens written to the prompt cache (Anthropic). Empty bills them at the input price. */
  cacheWrite: string
  output: string
  /** USD per {@link audioUnit} of input audio, for models billed by duration. Empty means 0. */
  audio: string
  audioUnit: AudioUnit
  /** USD per 1M audio input/output tokens (realtime, gpt-4o-transcribe). Empty means 0: billed as text tokens. */
  audioIn: string
  audioOut: string
  /** USD per generated picture, for image models billed per picture. Empty means 0. */
  image: string
  from: string
}

export type PriceErrors = Partial<Record<keyof PriceDraft, string>>

export const emptyPriceDraft = (): PriceDraft => ({ input: '', cached: '', cacheWrite: '', output: '', audio: '', audioUnit: 'minute', audioIn: '', audioOut: '', image: '', from: '' })

export const isPriceDraftEmpty = (d: PriceDraft): boolean =>
  !d.input.trim() && !d.cached.trim() && !d.cacheWrite.trim() && !d.output.trim() && !d.audio.trim() && !d.audioIn.trim() && !d.audioOut.trim() && !d.image.trim() && !d.from.trim()

const parse = (value: string): number => Number(value.replace(',', '.'))
const invalid = (value: string): boolean => value.trim() === '' || !Number.isFinite(parse(value)) || parse(value) < 0

export function validatePrice(d: PriceDraft): PriceErrors {
  const errors: PriceErrors = {}
  const message = 'Enter a price of 0 or more.'
  if (invalid(d.input)) errors.input = message
  if (invalid(d.cached)) errors.cached = message
  if (invalid(d.output)) errors.output = message
  if (d.cacheWrite.trim() && invalid(d.cacheWrite)) errors.cacheWrite = message
  if (d.audio.trim() && invalid(d.audio)) errors.audio = message
  if (d.audioIn.trim() && invalid(d.audioIn)) errors.audioIn = message
  if (d.audioOut.trim() && invalid(d.audioOut)) errors.audioOut = message
  if (d.image.trim() && invalid(d.image)) errors.image = message
  if (d.from && Number.isNaN(new Date(d.from).getTime())) errors.from = 'Enter a valid date and time.'
  return errors
}

/** A price per second or hour as the price per minute the API stores (ten decimals, so per hour round-trips). */
export const perMinute = (usd: number, unit: AudioUnit): number => Number((usd / MINUTES[unit]).toFixed(10))

/**
 * A stored per-minute price in the unit it reads best in: the first of minute, hour and second that needs at most
 * four decimals, e.g. `0,006 per min`, `0,40 per hour`. Falls back to per minute with up to eight decimals.
 */
export function formatAudioPrice(usdPerMinute: number): string {
  const unit = (['minute', 'hour', 'second'] as const).find((u) => {
    const value = usdPerMinute * MINUTES[u]
    return Math.abs(value - Number(value.toFixed(4))) < 1e-8 && value >= 0.0001
  })
  return unit ? `${formatUsd(usdPerMinute * MINUTES[unit])} ${AUDIO_UNIT_SHORT[unit]}` : `${usdPerMinute.toLocaleString('sv-SE', { minimumFractionDigits: 2, maximumFractionDigits: 8 })} ${AUDIO_UNIT_SHORT.minute}`
}

export function toNewPrice(d: PriceDraft): NewPrice {
  return {
    inputPerMillionUsd: parse(d.input),
    cachedInputPerMillionUsd: parse(d.cached),
    outputPerMillionUsd: parse(d.output),
    audioPerMinuteUsd: d.audio.trim() ? perMinute(parse(d.audio), d.audioUnit) : 0,
    audioInputPerMillionUsd: d.audioIn.trim() ? parse(d.audioIn) : 0,
    audioOutputPerMillionUsd: d.audioOut.trim() ? parse(d.audioOut) : 0,
    perImageUsd: d.image.trim() ? parse(d.image) : 0,
    cacheWriteInputPerMillionUsd: d.cacheWrite.trim() ? parse(d.cacheWrite) : null,
    effectiveFrom: d.from ? new Date(d.from).toISOString() : null,
  }
}
