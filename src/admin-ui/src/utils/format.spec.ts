import { describe, expect, it } from 'vitest'
import { formatSek } from './format'

describe('formatSek', () => {
  it('shows öre so small costs are visible', () => {
    expect(formatSek(0.004)).toBe('0,00 kr')
    expect(formatSek(0.125)).toBe('0,13 kr')
    expect(formatSek(48210.3)).toBe('48 210,30 kr')
    expect(formatSek(null)).toBe('—')
  })
})
