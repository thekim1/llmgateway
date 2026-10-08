/** AI Gateway admin — Tailwind config (v3 syntax; in v4 load with `@config "./tailwind.config.js";`).
 *  All colors resolve to CSS variables in tokens.css, so the three themes switch with
 *  <html data-theme="light|dark|lumen"> and no `dark:` variants are needed. Never hard-code hex in components. */
const v = (name) => `var(--${name})`;

module.exports = {
  content: ['./index.html', './src/**/*.{vue,ts}'],
  theme: {
    colors: {
      transparent: 'transparent',
      current: 'currentColor',
      white: '#FFFFFF',
      canvas: v('canvas'),
      sidebar: v('sidebar'),
      surface: { DEFAULT: v('surface'), 2: v('surface-2') },
      overlay: v('overlay'),
      sunken: v('sunken'),
      hover: v('hover'),
      scrim: v('scrim'),
      border: { DEFAULT: v('border'), strong: v('border-strong'), input: v('border-input') },
      fg: { DEFAULT: v('fg'), 2: v('fg-2'), 3: v('fg-3') },
      'on-fg': v('on-fg'),
      accent: { DEFAULT: v('accent'), hover: v('accent-hover'), fg: v('accent-fg'), soft: v('accent-soft'), ink: v('accent-ink') },
      ok: { DEFAULT: v('ok'), soft: v('ok-soft') },
      warn: { DEFAULT: v('warn'), soft: v('warn-soft') },
      danger: { DEFAULT: v('danger'), soft: v('danger-soft') },
      res: { onprem: v('res-onprem'), eu: v('res-eu'), ext: v('res-ext') },
      chart: { DEFAULT: v('chart'), muted: v('chart-muted') },
      focus: v('focus'),
    },
    fontFamily: {
      sans: ['Geist', 'ui-sans-serif', 'system-ui', 'sans-serif'],
      mono: ['"Geist Mono"', 'ui-monospace', 'monospace'],
      icon: ['"Material Symbols Rounded"'],
    },
    fontSize: {
      caption: ['12px', { lineHeight: '16px', fontWeight: '500' }],
      small: ['13px', { lineHeight: '18px' }],
      body: ['14px', { lineHeight: '20px' }],
      'body-lg': ['15px', { lineHeight: '20px' }],   // mobile body
      heading: ['16px', { lineHeight: '22px', fontWeight: '600' }],
      'title-3': ['20px', { lineHeight: '26px', fontWeight: '600', letterSpacing: '-0.015em' }],
      'title-2': ['22px', { lineHeight: '28px', fontWeight: '600', letterSpacing: '-0.02em' }],
      'title-1': ['30px', { lineHeight: '36px', fontWeight: '600', letterSpacing: '-0.025em' }],
      metric: ['34px', { lineHeight: '40px', fontWeight: '600', letterSpacing: '-0.03em' }],
      display: ['44px', { lineHeight: '48px', fontWeight: '600', letterSpacing: '-0.03em' }],
    },
    spacing: {
      0: '0', px: '1px', 0.5: '2px', 1: '4px', 1.5: '6px', 2: '8px', 2.5: '10px', 3: '12px', 3.5: '14px',
      4: '16px', 5: '20px', 6: '24px', 7: '28px', 8: '32px', 9: '36px', 10: '40px', 11: '44px',
      12: '48px', 14: '56px', 16: '64px', 20: '80px',
      sidebar: '248px', drawer: '480px', 'drawer-form': '520px', dialog: '480px',
    },
    borderRadius: {
      none: '0', chip: '8px', control: '10px', tile: '14px', card: v('r-card'), dialog: '24px', sheet: '32px', full: '9999px',
    },
    boxShadow: { none: 'none', 1: v('shadow-1'), 2: v('shadow-2'), 'inset-strong': `inset 0 0 0 1px ${v('border-strong')}`, 'inset-input': `inset 0 0 0 1px ${v('border-input')}`, 'inset-error': `inset 0 0 0 2px ${v('danger')}`, 'inset-selected': `inset 0 0 0 2px ${v('accent')}` },
    backdropBlur: { material: '28px' },
    extend: {
      height: { 'ctl-sm': '32px', ctl: '36px', 'ctl-lg': '44px', 'ctl-xl': '50px', badge: '22px', topbar: '60px', row: '60px' },
      maxWidth: { content: '1320px', drawer: '480px', 'drawer-form': '520px' },
      zIndex: { topbar: '10', drawer: '40', dialog: '50', toast: '60' },
      transitionTimingFunction: { emphasized: 'cubic-bezier(0.2, 0, 0, 1)' },
      transitionDuration: { fast: '120ms', base: '200ms', slow: '320ms' },
    },
  },
  plugins: [
    // `material` = theme-aware frosted surface. In light/dark --blur is `none`, so it is a no-op.
    ({ addUtilities }) => addUtilities({
      '.material': { backgroundColor: v('surface'), backdropFilter: v('blur'), boxShadow: v('shadow-1') },
      '.material-overlay': { backgroundColor: v('overlay'), backdropFilter: v('blur'), boxShadow: v('shadow-2') },
      '.bg-canvas-fixed': { background: v('canvas'), backgroundAttachment: 'fixed' },
      '.tabular': { fontVariantNumeric: 'tabular-nums' },
      '.sr-only-focusable:not(:focus)': { position: 'absolute', width: '1px', height: '1px', overflow: 'hidden', clip: 'rect(0 0 0 0)' },
    }),
  ],
};
