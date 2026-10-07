/** Parsing helpers for text inputs (inputmode numeric/decimal; accepts Swedish decimal comma). */

export function isBlank(value: string | null | undefined): boolean {
  return value === null || value === undefined || value.trim() === ''
}

/** Returns null for blank, NaN for invalid. */
export function parseDecimal(value: string | number | null | undefined): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : NaN
  if (isBlank(value)) return null
  const normalised = (value as string).trim().replace(/\s/g, '').replace(',', '.')
  if (!/^-?\d+(\.\d+)?$/.test(normalised)) return NaN
  return Number(normalised)
}

export function parseInteger(value: string | number | null | undefined): number | null {
  const parsed = parseDecimal(value)
  if (parsed === null) return null
  return Number.isInteger(parsed) ? parsed : NaN
}

export function isValidUrl(value: string): boolean {
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

/** `<input type="date">` value (YYYY-MM-DD) → ISO timestamp at end of that day, Stockholm time is approximated by local time. */
export function dateInputToIso(value: string): string | null {
  if (isBlank(value)) return null
  const date = new Date(`${value}T23:59:59`)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

export function isoToDateInput(value: string | null | undefined): string {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

export function numberToInput(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value).replace('.', ',')
}
