import type { NewPrice } from '@/api/types'

export interface PriceDraft {
  input: string
  cached: string
  output: string
  from: string
}

export type PriceErrors = Partial<Record<keyof PriceDraft, string>>

export const emptyPriceDraft = (): PriceDraft => ({ input: '', cached: '', output: '', from: '' })

export const isPriceDraftEmpty = (d: PriceDraft): boolean => !d.input.trim() && !d.cached.trim() && !d.output.trim() && !d.from.trim()

const parse = (value: string): number => Number(value.replace(',', '.'))
const invalid = (value: string): boolean => value.trim() === '' || !Number.isFinite(parse(value)) || parse(value) < 0

export function validatePrice(d: PriceDraft): PriceErrors {
  const errors: PriceErrors = {}
  const message = 'Enter a price of 0 or more.'
  if (invalid(d.input)) errors.input = message
  if (invalid(d.cached)) errors.cached = message
  if (invalid(d.output)) errors.output = message
  if (d.from && Number.isNaN(new Date(d.from).getTime())) errors.from = 'Enter a valid date and time.'
  return errors
}

export function toNewPrice(d: PriceDraft): NewPrice {
  return {
    inputPerMillionUsd: parse(d.input),
    cachedInputPerMillionUsd: parse(d.cached),
    outputPerMillionUsd: parse(d.output),
    effectiveFrom: d.from ? new Date(d.from).toISOString() : null,
  }
}
