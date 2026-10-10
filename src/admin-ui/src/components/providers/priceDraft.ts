import type { NewPrice } from '@/api/types'

export interface PriceDraft {
  input: string
  cached: string
  output: string
  /** USD per minute of audio, for speech-to-text models billed by duration. Empty means 0. */
  audio: string
  /** USD per 1M audio input/output tokens (realtime, gpt-4o-transcribe). Empty means 0: billed as text tokens. */
  audioIn: string
  audioOut: string
  from: string
}

export type PriceErrors = Partial<Record<keyof PriceDraft, string>>

export const emptyPriceDraft = (): PriceDraft => ({ input: '', cached: '', output: '', audio: '', audioIn: '', audioOut: '', from: '' })

export const isPriceDraftEmpty = (d: PriceDraft): boolean =>
  !d.input.trim() && !d.cached.trim() && !d.output.trim() && !d.audio.trim() && !d.audioIn.trim() && !d.audioOut.trim() && !d.from.trim()

const parse = (value: string): number => Number(value.replace(',', '.'))
const invalid = (value: string): boolean => value.trim() === '' || !Number.isFinite(parse(value)) || parse(value) < 0

export function validatePrice(d: PriceDraft): PriceErrors {
  const errors: PriceErrors = {}
  const message = 'Enter a price of 0 or more.'
  if (invalid(d.input)) errors.input = message
  if (invalid(d.cached)) errors.cached = message
  if (invalid(d.output)) errors.output = message
  if (d.audio.trim() && invalid(d.audio)) errors.audio = message
  if (d.audioIn.trim() && invalid(d.audioIn)) errors.audioIn = message
  if (d.audioOut.trim() && invalid(d.audioOut)) errors.audioOut = message
  if (d.from && Number.isNaN(new Date(d.from).getTime())) errors.from = 'Enter a valid date and time.'
  return errors
}

export function toNewPrice(d: PriceDraft): NewPrice {
  return {
    inputPerMillionUsd: parse(d.input),
    cachedInputPerMillionUsd: parse(d.cached),
    outputPerMillionUsd: parse(d.output),
    audioPerMinuteUsd: d.audio.trim() ? parse(d.audio) : 0,
    audioInputPerMillionUsd: d.audioIn.trim() ? parse(d.audioIn) : 0,
    audioOutputPerMillionUsd: d.audioOut.trim() ? parse(d.audioOut) : 0,
    effectiveFrom: d.from ? new Date(d.from).toISOString() : null,
  }
}
