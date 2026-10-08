export const TIME_ZONE = 'Europe/Stockholm'
const NUMBER_LOCALE = 'sv-SE'

const numberFormatter = new Intl.NumberFormat(NUMBER_LOCALE)
const decimalFormatter = new Intl.NumberFormat(NUMBER_LOCALE, { maximumFractionDigits: 1 })
const sekFractionFormatter = new Intl.NumberFormat(NUMBER_LOCALE, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const usdFormatter = new Intl.NumberFormat(NUMBER_LOCALE, { minimumFractionDigits: 2, maximumFractionDigits: 4 })
const dayFormatter = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, day: 'numeric', month: 'short', year: 'numeric' })
const shortDayFormatter = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, day: 'numeric', month: 'short' })
const timeFormatter = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, hour: '2-digit', minute: '2-digit', hour12: false })
const timeSecondsFormatter = new Intl.DateTimeFormat('en-GB', {
  timeZone: TIME_ZONE,
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
})

const DASH = '—'
const NBSP = '\u00A0'

function isMissing(value: number | null | undefined): value is null | undefined {
  return value === null || value === undefined || Number.isNaN(value)
}

/** Swedish number formatting uses NBSP thousands separators; normalise any narrow variants. */
function nbsp(text: string): string {
  return text.replace(/[\u00A0\u202F]/g, NBSP).replace(/\u2212/g, '-')
}

/** SEK amount with `kr` suffix, e.g. `48 210 kr`. */
export function formatSek(value: number | null | undefined, fractionDigits = 0): string {
  if (isMissing(value)) return DASH
  const text = fractionDigits > 0 ? sekFractionFormatter.format(value) : numberFormatter.format(Math.round(value))
  return `${nbsp(text)}${NBSP}kr`
}

export function formatUsd(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  return nbsp(usdFormatter.format(value))
}

export function formatNumber(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  return nbsp(numberFormatter.format(value))
}

/** Compact count, e.g. `1.2M`, for tight spaces. */
export function formatCompact(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  const abs = Math.abs(value)
  if (abs >= 1_000_000) return `${decimalFormatter.format(value / 1_000_000)}M`
  if (abs >= 10_000) return `${decimalFormatter.format(value / 1_000)}k`
  return formatNumber(value)
}

/** Formats a value already expressed in percent (0-100). */
export function formatPercent(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  return `${nbsp(decimalFormatter.format(value))}%`
}

/** Formats a ratio (0-1) as percent. */
export function formatRatio(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  return formatPercent(value * 100)
}

export function formatMs(value: number | null | undefined): string {
  if (isMissing(value)) return DASH
  if (value >= 1000) return `${decimalFormatter.format(value / 1000)}${NBSP}s`
  return `${numberFormatter.format(Math.round(value))}${NBSP}ms`
}

function toDate(value: string | Date | null | undefined): Date | null {
  if (!value) return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** `8 Oct 2026`. */
export function formatDate(value: string | Date | null | undefined): string {
  const date = toDate(value)
  return date ? dayFormatter.format(date).replace('Sept', 'Sep') : DASH
}

/** `8 Oct`. */
export function formatShortDate(value: string | Date | null | undefined): string {
  const date = toDate(value)
  return date ? shortDayFormatter.format(date).replace('Sept', 'Sep') : DASH
}

/** `14:32`, 24 h Europe/Stockholm. */
export function formatTime(value: string | Date | null | undefined, seconds = false): string {
  const date = toDate(value)
  return date ? (seconds ? timeSecondsFormatter : timeFormatter).format(date) : DASH
}

/** `8 Oct 2026, 14:32`. */
export function formatDateTime(value: string | Date | null | undefined, seconds = false): string {
  const date = toDate(value)
  return date ? `${formatDate(date)}, ${formatTime(date, seconds)}` : DASH
}

/** Relative time for anything under 7 days ("2 min ago"), absolute date otherwise. */
export function formatRelative(value: string | Date | null | undefined, now: Date = new Date()): string {
  const date = toDate(value)
  if (!date) return DASH
  const seconds = Math.round((now.getTime() - date.getTime()) / 1000)
  if (seconds < 0) return formatDate(date)
  if (seconds < 45) return 'Just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  const days = Math.round(hours / 24)
  if (days === 1) return 'Yesterday'
  if (days < 7) return `${days} days ago`
  return formatDate(date)
}

/** Time until a future instant, e.g. `6 h 12 min`. */
export function formatDuration(ms: number): string {
  const totalMinutes = Math.max(0, Math.floor(ms / 60000))
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return hours > 0 ? `${hours} h ${minutes} min` : `${minutes} min`
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

export function initials(name: string | null | undefined): string {
  if (!name) return '?'
  const parts = name.trim().split(/\s+/).filter(Boolean)
  const letters = parts.length > 1 ? [parts[0]?.[0], parts[parts.length - 1]?.[0]] : [parts[0]?.slice(0, 2)]
  return letters.filter(Boolean).join('').toUpperCase() || '?'
}
