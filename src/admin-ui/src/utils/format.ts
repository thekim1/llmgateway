export const TIME_ZONE = 'Europe/Stockholm'
export const NUMBER_LOCALE = 'sv-SE'

const sekFormatter = new Intl.NumberFormat(NUMBER_LOCALE, { style: 'currency', currency: 'SEK' })
const sekWholeFormatter = new Intl.NumberFormat(NUMBER_LOCALE, {
  style: 'currency',
  currency: 'SEK',
  maximumFractionDigits: 0,
})
const usdFormatter = new Intl.NumberFormat(NUMBER_LOCALE, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})
const numberFormatter = new Intl.NumberFormat(NUMBER_LOCALE)
const decimalFormatter = new Intl.NumberFormat(NUMBER_LOCALE, { maximumFractionDigits: 1 })
const dateFormatter = new Intl.DateTimeFormat(NUMBER_LOCALE, { timeZone: TIME_ZONE, dateStyle: 'short' })
const dateTimeFormatter = new Intl.DateTimeFormat(NUMBER_LOCALE, {
  timeZone: TIME_ZONE,
  dateStyle: 'short',
  timeStyle: 'short',
})
const dateTimeSecondsFormatter = new Intl.DateTimeFormat(NUMBER_LOCALE, {
  timeZone: TIME_ZONE,
  dateStyle: 'short',
  timeStyle: 'medium',
})

function isMissing(value: number | null | undefined): value is null | undefined {
  return value === null || value === undefined || Number.isNaN(value)
}

export function formatSek(value: number | null | undefined, whole = false): string {
  if (isMissing(value)) return '–'
  return (whole ? sekWholeFormatter : sekFormatter).format(value)
}

export function formatUsd(value: number | null | undefined): string {
  if (isMissing(value)) return '–'
  return usdFormatter.format(value)
}

export function formatNumber(value: number | null | undefined): string {
  if (isMissing(value)) return '–'
  return numberFormatter.format(value)
}

/** Formats a value already expressed in percent (0–100). */
export function formatPercent(value: number | null | undefined): string {
  if (isMissing(value)) return '–'
  return `${decimalFormatter.format(value)} %`
}

/** Formats a ratio (0–1) as percent. */
export function formatRatio(value: number | null | undefined): string {
  if (isMissing(value)) return '–'
  return formatPercent(value * 100)
}

export function formatMs(value: number | null | undefined): string {
  if (isMissing(value)) return '–'
  return `${numberFormatter.format(Math.round(value))} ms`
}

function toDate(value: string | Date | null | undefined): Date | null {
  if (!value) return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatDate(value: string | Date | null | undefined): string {
  const date = toDate(value)
  return date ? dateFormatter.format(date) : '–'
}

export function formatDateTime(value: string | Date | null | undefined, seconds = false): string {
  const date = toDate(value)
  return date ? (seconds ? dateTimeSecondsFormatter : dateTimeFormatter).format(date) : '–'
}

/** YYYY-MM-DD for the given instant in Europe/Stockholm (for `<input type="date">`). */
export function toIsoDate(value: Date): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: TIME_ZONE,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(value)
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? ''
  return `${get('year')}-${get('month')}-${get('day')}`
}

export function startOfMonthIso(now: Date = new Date()): string {
  return `${toIsoDate(now).slice(0, 8)}01`
}

export function todayIso(now: Date = new Date()): string {
  return toIsoDate(now)
}

/** Converts a USD amount to SEK using the given rate. */
export function usdToSek(usd: number | null | undefined, sekPerUsd: number | null | undefined): number | null {
  if (isMissing(usd) || isMissing(sekPerUsd)) return null
  return usd * sekPerUsd
}

export function clampPercent(value: number): number {
  if (!Number.isFinite(value)) return 0
  return Math.max(0, Math.min(100, value))
}
