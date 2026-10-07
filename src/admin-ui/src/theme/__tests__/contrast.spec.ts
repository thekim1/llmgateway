import { describe, expect, it } from 'vitest'
import { contrastRatio, relativeLuminance } from '../contrast'
import { THEME_IDS, themes, type ThemeTokens } from '../themes'

type Token = keyof ThemeTokens
type Pair = [fg: Token, bg: Token, min: number]

/** WCAG 2.2 AAA: 7:1 for text (1.4.6), 3:1 for non-text UI components and focus (1.4.11, 2.4.13). */
const AAA_TEXT = 7
const NON_TEXT = 3

const pairs: Pair[] = [
  ['text', 'bg', AAA_TEXT],
  ['text', 'surface', AAA_TEXT],
  ['text', 'surfaceRaised', AAA_TEXT],
  ['textMuted', 'bg', AAA_TEXT],
  ['textMuted', 'surface', AAA_TEXT],
  ['textMuted', 'surfaceRaised', AAA_TEXT],
  ['link', 'bg', AAA_TEXT],
  ['link', 'surface', AAA_TEXT],
  ['link', 'surfaceRaised', AAA_TEXT],
  ['primaryContrast', 'primary', AAA_TEXT],
  ['primaryContrast', 'primaryHover', AAA_TEXT],
  ['primary', 'bg', AAA_TEXT],
  ['success', 'successBg', AAA_TEXT],
  ['warning', 'warningBg', AAA_TEXT],
  ['danger', 'dangerBg', AAA_TEXT],
  ['info', 'infoBg', AAA_TEXT],
  ['danger', 'bg', AAA_TEXT],
  ['success', 'bg', AAA_TEXT],
  ['warning', 'bg', AAA_TEXT],
  ['info', 'bg', AAA_TEXT],
  ['residencyOnPrem', 'residencyOnPremBg', AAA_TEXT],
  ['residencyEu', 'residencyEuBg', AAA_TEXT],
  ['residencyExternal', 'residencyExternalBg', AAA_TEXT],
  ['headerText', 'headerBg', AAA_TEXT],
  ['footerText', 'footerBg', AAA_TEXT],
  ['navActiveText', 'navActiveBg', AAA_TEXT],
  ['codeText', 'codeBg', AAA_TEXT],
  ['borderStrong', 'bg', NON_TEXT],
  ['borderStrong', 'surface', NON_TEXT],
  ['focusRing', 'bg', NON_TEXT],
  ['focusRing', 'surface', NON_TEXT],
  ['focusRing', 'surfaceRaised', NON_TEXT],
  ['focusRing', 'headerBg', NON_TEXT],
  ['chart1', 'bg', NON_TEXT],
  ['chart2', 'bg', NON_TEXT],
  ['chart3', 'bg', NON_TEXT],
  ['chart4', 'bg', NON_TEXT],
  ['chart1', 'chartTrack', NON_TEXT],
  ['primary', 'bg', NON_TEXT],
]

describe('contrast helpers', () => {
  it('computes known reference values', () => {
    expect(relativeLuminance('#FFFFFF')).toBeCloseTo(1, 5)
    expect(relativeLuminance('#000000')).toBeCloseTo(0, 5)
    expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 5)
    expect(contrastRatio('#FFFFFF', '#FFFFFF')).toBeCloseTo(1, 5)
    // Brand mörkgrön on white is ~6.5:1, which is why it is not used for normal text.
    expect(contrastRatio('#006E1E', '#FFFFFF')).toBeLessThan(7)
    expect(contrastRatio('#006E1E', '#FFFFFF')).toBeGreaterThan(6)
  })
})

describe.each(THEME_IDS)('theme %s', (id) => {
  const tokens = themes[id].tokens

  it.each(pairs)('%s on %s ≥ %d:1', (fg, bg, min) => {
    const ratio = contrastRatio(tokens[fg], tokens[bg])
    expect(
      ratio,
      `${id}: ${fg} (${tokens[fg]}) on ${bg} (${tokens[bg]}) = ${ratio.toFixed(2)}:1, kräver ${min}:1`,
    ).toBeGreaterThanOrEqual(min)
  })

  it('defines every token as a valid colour', () => {
    for (const [token, value] of Object.entries(tokens)) {
      if (token === 'overlay') continue
      expect(value, token).toMatch(/^#[0-9A-Fa-f]{6}$/)
    }
  })
})
