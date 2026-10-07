/**
 * Design tokens for the admin UI. Every theme defines the same set of semantic tokens. They are
 * applied as CSS custom properties (`--color-<kebab-name>`) on `<html data-theme="…">`.
 *
 * The contrast test in `__tests__/contrast.spec.ts` verifies WCAG 2.2 AAA ratios for every theme.
 *
 * Brand reference (Umeå kommun, 2019): mörkgrön #006E1E, gråsvart #555555, brisvit #D1E8FF,
 * rosa #E4B1C2. #006E1E on white is only ~6.5:1, so a darker green (#00561A, ~9:1) is used for text,
 * links and primary buttons in the light theme.
 */

export const THEME_IDS = ['umea-light', 'umea-dark', 'hc-light', 'hc-dark'] as const
export type ThemeId = (typeof THEME_IDS)[number]
export type ThemePreference = ThemeId | 'system'
export const THEME_PREFERENCES: readonly ThemePreference[] = ['system', ...THEME_IDS]

export interface ThemeTokens {
  bg: string
  surface: string
  surfaceRaised: string
  text: string
  textMuted: string
  border: string
  borderStrong: string
  primary: string
  primaryHover: string
  primaryContrast: string
  link: string
  focusRing: string
  success: string
  successBg: string
  warning: string
  warningBg: string
  danger: string
  dangerBg: string
  info: string
  infoBg: string
  residencyOnPrem: string
  residencyOnPremBg: string
  residencyEu: string
  residencyEuBg: string
  residencyExternal: string
  residencyExternalBg: string
  chart1: string
  chart2: string
  chart3: string
  chart4: string
  chartTrack: string
  headerBg: string
  headerText: string
  headerAccent: string
  footerBg: string
  footerText: string
  navActiveBg: string
  navActiveText: string
  codeBg: string
  codeText: string
  /** Not a contrast-relevant colour (backdrop behind dialogs). */
  overlay: string
}

export interface ThemeDefinition {
  id: ThemeId
  colorScheme: 'light' | 'dark'
  highContrast: boolean
  tokens: ThemeTokens
}

const umeaLight: ThemeTokens = {
  bg: '#FFFFFF',
  surface: '#F4F6F4',
  surfaceRaised: '#FFFFFF',
  text: '#262626',
  textMuted: '#4D4D4D',
  border: '#C4CAC4',
  borderStrong: '#5E655E',
  primary: '#00561A',
  primaryHover: '#003F13',
  primaryContrast: '#FFFFFF',
  link: '#00561A',
  focusRing: '#7A1E48',
  success: '#00561A',
  successBg: '#E3F1E6',
  warning: '#6B4300',
  warningBg: '#FFF1CC',
  danger: '#8C0018',
  dangerBg: '#FCE8EC',
  info: '#00427A',
  infoBg: '#E1EFFF',
  residencyOnPrem: '#00561A',
  residencyOnPremBg: '#E3F1E6',
  residencyEu: '#00427A',
  residencyEuBg: '#D1E8FF',
  residencyExternal: '#4A0B24',
  residencyExternalBg: '#E4B1C2',
  chart1: '#00561A',
  chart2: '#00427A',
  chart3: '#7A1E48',
  chart4: '#6B4300',
  chartTrack: '#E4E8E4',
  headerBg: '#FFFFFF',
  headerText: '#262626',
  headerAccent: '#006E1E',
  footerBg: '#00561A',
  footerText: '#FFFFFF',
  navActiveBg: '#E3F1E6',
  navActiveText: '#00561A',
  codeBg: '#F4F6F4',
  codeText: '#1E1E1E',
  overlay: 'rgba(0, 0, 0, 0.55)',
}

const umeaDark: ThemeTokens = {
  bg: '#121412',
  surface: '#1B1F1B',
  surfaceRaised: '#242924',
  text: '#F1F3F1',
  textMuted: '#C5CBC5',
  border: '#3A413A',
  borderStrong: '#8A938A',
  primary: '#6FD08C',
  primaryHover: '#8FDDA6',
  primaryContrast: '#062010',
  link: '#7FD99A',
  focusRing: '#F5B7CC',
  success: '#8FE0A8',
  successBg: '#10301A',
  warning: '#FFD27A',
  warningBg: '#33260A',
  danger: '#FFB4BE',
  dangerBg: '#3B0F16',
  info: '#A8D1FF',
  infoBg: '#0E2640',
  residencyOnPrem: '#8FE0A8',
  residencyOnPremBg: '#10301A',
  residencyEu: '#A8D1FF',
  residencyEuBg: '#0E2640',
  residencyExternal: '#F5B7CC',
  residencyExternalBg: '#3D1426',
  chart1: '#6FD08C',
  chart2: '#7FB8F0',
  chart3: '#F09AB8',
  chart4: '#E8C060',
  chartTrack: '#2E342E',
  headerBg: '#1B1F1B',
  headerText: '#F1F3F1',
  headerAccent: '#6FD08C',
  footerBg: '#0E2A15',
  footerText: '#FFFFFF',
  navActiveBg: '#10301A',
  navActiveText: '#8FE0A8',
  codeBg: '#0B0D0B',
  codeText: '#F1F3F1',
  overlay: 'rgba(0, 0, 0, 0.7)',
}

const hcLight: ThemeTokens = {
  bg: '#FFFFFF',
  surface: '#FFFFFF',
  surfaceRaised: '#FFFFFF',
  text: '#000000',
  textMuted: '#1A1A1A',
  border: '#000000',
  borderStrong: '#000000',
  primary: '#003D12',
  primaryHover: '#000000',
  primaryContrast: '#FFFFFF',
  link: '#003D12',
  focusRing: '#000000',
  success: '#003D12',
  successBg: '#FFFFFF',
  warning: '#4A2E00',
  warningBg: '#FFFFFF',
  danger: '#6E0012',
  dangerBg: '#FFFFFF',
  info: '#00306B',
  infoBg: '#FFFFFF',
  residencyOnPrem: '#003D12',
  residencyOnPremBg: '#FFFFFF',
  residencyEu: '#00306B',
  residencyEuBg: '#FFFFFF',
  residencyExternal: '#4A0B24',
  residencyExternalBg: '#FFFFFF',
  chart1: '#000000',
  chart2: '#003D12',
  chart3: '#00306B',
  chart4: '#4A0B24',
  chartTrack: '#FFFFFF',
  headerBg: '#FFFFFF',
  headerText: '#000000',
  headerAccent: '#000000',
  footerBg: '#000000',
  footerText: '#FFFFFF',
  navActiveBg: '#000000',
  navActiveText: '#FFFFFF',
  codeBg: '#FFFFFF',
  codeText: '#000000',
  overlay: 'rgba(0, 0, 0, 0.8)',
}

const hcDark: ThemeTokens = {
  bg: '#000000',
  surface: '#000000',
  surfaceRaised: '#000000',
  text: '#FFFFFF',
  textMuted: '#E8E8E8',
  border: '#FFFFFF',
  borderStrong: '#FFFFFF',
  primary: '#8CFFA0',
  primaryHover: '#FFFFFF',
  primaryContrast: '#000000',
  link: '#FFFF00',
  focusRing: '#00FFFF',
  success: '#8CFFA0',
  successBg: '#000000',
  warning: '#FFFF00',
  warningBg: '#000000',
  danger: '#FF9EA8',
  dangerBg: '#000000',
  info: '#9ED0FF',
  infoBg: '#000000',
  residencyOnPrem: '#8CFFA0',
  residencyOnPremBg: '#000000',
  residencyEu: '#9ED0FF',
  residencyEuBg: '#000000',
  residencyExternal: '#FFB8D0',
  residencyExternalBg: '#000000',
  chart1: '#8CFFA0',
  chart2: '#9ED0FF',
  chart3: '#FF9EA8',
  chart4: '#FFFF00',
  chartTrack: '#000000',
  headerBg: '#000000',
  headerText: '#FFFFFF',
  headerAccent: '#FFFFFF',
  footerBg: '#000000',
  footerText: '#FFFFFF',
  navActiveBg: '#FFFF00',
  navActiveText: '#000000',
  codeBg: '#000000',
  codeText: '#FFFFFF',
  overlay: 'rgba(0, 0, 0, 0.85)',
}

export const themes: Record<ThemeId, ThemeDefinition> = {
  'umea-light': { id: 'umea-light', colorScheme: 'light', highContrast: false, tokens: umeaLight },
  'umea-dark': { id: 'umea-dark', colorScheme: 'dark', highContrast: false, tokens: umeaDark },
  'hc-light': { id: 'hc-light', colorScheme: 'light', highContrast: true, tokens: hcLight },
  'hc-dark': { id: 'hc-dark', colorScheme: 'dark', highContrast: true, tokens: hcDark },
}

export function isThemePreference(value: unknown): value is ThemePreference {
  return typeof value === 'string' && (THEME_PREFERENCES as readonly string[]).includes(value)
}

export function tokenToCssVar(token: string): string {
  return `--color-${token.replace(/[A-Z0-9]/g, (m) => `-${m.toLowerCase()}`)}`
}

export interface SystemPreferences {
  prefersDark: boolean
  prefersMoreContrast: boolean
}

export function readSystemPreferences(): SystemPreferences {
  const mm = typeof window !== 'undefined' ? window.matchMedia : undefined
  if (!mm) return { prefersDark: false, prefersMoreContrast: false }
  return {
    prefersDark: mm('(prefers-color-scheme: dark)').matches,
    prefersMoreContrast: mm('(prefers-contrast: more)').matches,
  }
}

export function resolveTheme(
  preference: ThemePreference,
  system: SystemPreferences = readSystemPreferences(),
): ThemeId {
  if (preference !== 'system') return preference
  if (system.prefersMoreContrast) return system.prefersDark ? 'hc-dark' : 'hc-light'
  return system.prefersDark ? 'umea-dark' : 'umea-light'
}

/** Applies the theme tokens as CSS custom properties on the root element and sets `data-theme`. */
export function applyTheme(id: ThemeId, root: HTMLElement = document.documentElement): void {
  const theme = themes[id]
  for (const [token, value] of Object.entries(theme.tokens)) {
    root.style.setProperty(tokenToCssVar(token), value)
  }
  root.style.setProperty('color-scheme', theme.colorScheme)
  root.dataset.theme = id
}
