# AI Gateway Admin — Design System (for LLM implementation)

You are implementing the admin UI for Umeå kommun's LLM gateway. Follow this document exactly.
Files: `tokens.css` (CSS variables, 3 themes, resets, focus ring) · `tailwind.config.js` (maps tokens to utilities).
Visual references: `Admin Desktop.dc.html`, `Admin Mobile.dc.html`, `Design System.dc.html`.

## 0. Hard rules
1. **Only tokens.** Never write hex/rgb in components. Use Tailwind classes from the config (`bg-surface`, `text-fg-2`, `rounded-card`).
2. **Themes** switch with `document.documentElement.dataset.theme = 'light' | 'dark' | 'lumen'`. Persist in `localStorage('ume-theme')`; default from `prefers-color-scheme` (dark → `dark`, else `light`). Never use `dark:` variants.
3. **WCAG 2.2 AA** is the legal floor (DOS-lagen → EN 301 549 → WCAG 2.1 AA; design to 2.2 AA). Token pairs already pass; don't invent new pairs.
4. **UI language is English.** Swedish proper nouns (förvaltning names, team names) and Swedish API error messages get `lang="sv"` on their element.
5. **One primary button per view.** Max two words per button label, one word/short phrase per badge.
6. **Metadata only.** Never show or imply prompt/response content anywhere.

## 1. Principles
- **Attention first.** Pages that can contain problems open with a *Needs attention* block (alerts, open circuits, expiring keys), then key figures, then detail.
- **Calm and powerful.** Large confident numbers, generous whitespace, one accent. Hierarchy comes from size and weight, not color or borders.
- **Show the consequence.** Before any destructive or irreversible action, say exactly what happens (`Requests fail with key_revoked`).
- **Explain disabled.** Disabled controls use `aria-disabled="true"` (stay focusable) and show the reason next to them.
- **Align everything.** 4 px grid; one column grid per page; numbers `tabular` and right-aligned; table cell padding identical across tables.

## 2. Tokens
| Group | Tokens | Use |
|---|---|---|
| Surfaces | `canvas` page · `sidebar` · `surface` cards · `surface-2` table head, nested tiles · `sunken` tracks, segmented bg, code · `overlay` drawers/dialogs · `scrim` | |
| Text | `on-fg` text on `fg` fills (toasts, logo mark) · `fg` primary · `fg-2` secondary · `fg-3` tertiary/captions (still ≥4.5:1) | Never lower contrast than `fg-3` |
| Lines | `border` dividers · `border-strong` secondary buttons · `border-input` form fields (≥3:1, WCAG 1.4.11) | |
| Accent | `accent` primary fill · `accent-hover` · `accent-fg` text on accent · `accent-soft` selected bg · `accent-ink` accent text/links | |
| Status | `ok`/`ok-soft`, `warn`/`warn-soft`, `danger`/`danger-soft` | Badge = `*-soft` bg + `*` text |
| Residency | `res-onprem` ■ · `res-eu` ● · `res-ext` ◆ | Always glyph + word |
| Data viz | `chart` current/primary · `chart-muted` history | |
| Effects | `shadow-1` cards · `shadow-2` overlays · `--blur` (Lumen only) · `--r-card` 16/22 px | Use `.material` / `.material-overlay` utilities |

### Themes
- **Light** — warm paper neutrals, white cards, indigo accent `#3446DB`.
- **Dark** — graphite (`#0D0D0F` canvas, `#17171B` cards), periwinkle accent `#8C98FF` with dark text on it. Hairline shadows only.
- **Lumen (2027)** — ambient light field (`canvas` is a soft multi-radial background; apply with `.bg-canvas-fixed`), frosted cards (`.material`, 76% white + 28 px blur), radius 22 px, inner top highlight. Glass is never under 74% opacity, so contrast does not depend on what is behind. Under `prefers-reduced-transparency: reduce` set `--blur:none` and use `--surface-2` for cards.

## 3. Type
Geist (UI) + Geist Mono (keys, prefixes, model/route/provider names, codes, ansvarskod). Load via Google Fonts, weights 400/500/600/700.

| Class | Size/line | Use |
|---|---|---|
| `text-display` | 44/48 600 | Hero number (rare) |
| `text-title-1` | 30/36 600 | Page title (one per page, `h1`) |
| `text-metric` | 34/40 600 | Metric tiles |
| `text-title-2` | 22/28 600 | Drawer / detail title |
| `text-title-3` | 20/26 600 | Dialog title |
| `text-heading` | 16/22 600 | Card & section headings (`h2`) |
| `text-body` | 14/20 | Default desktop |
| `text-body-lg` | 15/20 | Default mobile |
| `text-small` | 13/18 | Supporting text, hints |
| `text-caption` | 12/16 500 | Table headers, field group labels, badges |

No uppercase-tracked labels. Sentence case everywhere. `text-wrap: pretty` on paragraphs.

## 4. Layout
- **Desktop shell:** sticky sidebar `w-sidebar` (`bg-sidebar`, right border) + sticky top bar `h-topbar` (search, environment pill, session timer) + `main` `px-10 pt-9 pb-16 max-w-content`.
- **Page header:** `flex items-end justify-between gap-6 mb-6|mb-8` → left: `h1.text-title-1` + one-line `p.text-fg-2`; right: actions (primary last).
- **Grids:** `grid gap-3` with `repeat(auto-fit,minmax(220px,1fr))` for metric tiles; `minmax(240px,300px) 1fr` for master/detail (Organisation, Routes).
- **Breakpoints:** `<1024px` sidebar collapses to icon rail; `<768px` switch to mobile shell (bottom tabs). Content must reflow at 320 px CSS width / 400% zoom (WCAG 1.4.10) — tables become stacked cards below 768px.
- **Mobile shell:** 4 bottom tabs (Overview, Alerts, Keys, Health), `px-4`, controls `h-ctl-lg` (44) minimum, primary actions `h-ctl-xl` (50), destructive confirm as bottom sheet `rounded-sheet`. Mobile scope = monitoring + urgent actions only: acknowledge alert, revoke key, open/close circuit. Link to desktop for everything else.

## 5. Components (Tailwind recipes)
Use these verbatim. Icons: Material Symbols Rounded, 20 px (18 in buttons), `aria-hidden="true"`; FILL 1 only for active nav and status icons.

**Button**
- Primary: `inline-flex items-center gap-1.5 h-ctl px-3.5 rounded-control bg-accent text-accent-fg font-medium hover:bg-accent-hover`
- Secondary: `… bg-surface text-fg shadow-inset-strong hover:bg-surface-2`
- Quiet: `… bg-transparent text-fg hover:bg-hover`
- Danger (trigger): `… bg-transparent text-danger hover:bg-danger-soft` · Danger (confirm): `… bg-danger text-white`
- Sizes: `h-ctl-sm` (32, in cards/tables), `h-ctl` (36, page), `h-ctl-lg`/`h-ctl-xl` (mobile). Icon-only buttons need `aria-label` and are ≥32×32.

**Badge (status)** `inline-flex items-center gap-1.5 h-badge px-2 rounded-full text-caption bg-{tone}-soft text-{tone}` + leading 6 px dot `bg-current`. Labels:
| Enum | Label | Tone |
|---|---|---|
| KeyStatus.Active | Active | ok |
| InGracePeriod | Grace period | warn |
| Expired | Expired | neutral (`bg-sunken text-fg-2`) |
| Revoked | Revoked | danger |
| Disabled | Disabled | neutral |
| CircuitState.Open | Circuit open | danger |
| Provider isDrained | Drained | neutral |
| Health Healthy/Degraded/Unhealthy | Healthy / Degraded / Down | ok / warn / danger |

**Residency** `text-small text-res-{onprem|eu|ext}`: `■ On-prem`, `● EU`, `◆ External` (glyph `aria-hidden`).
**PII policy labels:** Off, Allow, Redact, Block, On-prem (= `RerouteToOnPrem`); always show the one-line explanation of the selected policy below the control.

**Card** `material rounded-card p-5` (detail cards `p-6`). Card heading `text-heading` with optional right-aligned `text-small text-fg-3` meta.
**Metric tile** card + `text-small text-fg-2 font-medium` label → `text-metric tabular` value → `text-small text-fg-3` context. Optional 6 px meter.
**Attention card** card with 32 px tinted icon square (`rounded-control bg-{tone}-soft text-{tone}`), kind caption in tone color, `h3` title, one-sentence body, secondary `h-ctl-sm` action. Max 3 shown; link to the rest.
**Table** inside `material rounded-card overflow-hidden`. `thead` `bg-surface-2 text-caption text-fg-3`, cells `px-3`, first/last `px-5`; rows `h-row border-t border-border hover:bg-hover`. First cell is `th scope="row"` containing a real `<button>` that opens detail (whole row also clickable). Primary text `font-medium`, secondary line `text-caption text-fg-3`. Numbers right-aligned `tabular`. Empty state: one sentence + "Clear filters".
**Segmented control** `flex gap-0.5 p-[3px] rounded-control bg-sunken`; item `h-[30px] px-3 rounded-chip text-small font-medium`; selected `bg-surface shadow-1 text-fg`, others `text-fg-2`. Use `aria-pressed` (filters) or `role=radiogroup/radio` (form values). Counts in `text-fg-3`.
**Text field** label `font-medium` above, `h-10 px-3 rounded-control bg-surface shadow-inset-input`; error → `shadow-inset-error aria-invalid="true"` + message `text-small text-danger` with `error` icon, linked via `aria-describedby`. Hint text `text-small text-fg-3`. Never placeholder-as-label.
**Toggle chip (multi-select)** `h-ctl px-3 rounded-control` off: `bg-surface shadow-inset-input`; on: `bg-accent-soft shadow-inset-selected` + `check_circle` icon. `aria-pressed`.
**Meter** track `h-2 rounded-full bg-sunken`, fill `chart` (<80%), `warn` (80–99%), `danger` (≥100%). Threshold ticks: 2 px `bg-fg-3/50`. `role="meter"` with `aria-valuenow/min/max` and `aria-label`. Always print the % next to it.
**Drawer (detail / form)** right side, `w-drawer` (forms `w-drawer-form`), `material-overlay`, full height; header (badge, `title-2`, mono prefix, close), scrolling body of `dl` sections (`text-caption` section titles, rows `grid-cols-[150px_1fr] py-2.5 border-b`), sticky footer actions (primary left, danger right).
**Dialog** centered `w-dialog p-7 rounded-dialog material-overlay`; 40 px tinted icon for danger/success, `title-3`, consequence paragraph, actions right (Cancel then confirm).
**Secret reveal** dialog: mono secret in `bg-sunken rounded-control`, Copy button (label flips to "Copied", announced), checkbox "I've stored the secret somewhere safe" gates Done; scrim click/Esc does nothing until checked.
**Toast** bottom-center, `bg-fg text-on-fg rounded-tile px-4 py-3 shadow-2`, inside a persistent `role="status" aria-live="polite"` region, auto-hide 4 s (pause on hover/focus). Errors are never toasts — show them inline.
**Nav item** `h-[34px] px-2.5 rounded-control gap-2.5`; current `bg-surface shadow-1 text-fg font-semibold aria-current="page"` + filled icon; others `text-fg-2 hover:bg-hover`. Count badge `bg-danger text-white` with `aria-label="2 open"`.

## 6. Page patterns
- **Overview:** Needs attention → 4 metric tiles (Spend this month vs budgets, Requests 24 h, Error rate 24 h, Latency p95) → Daily spend bar chart (30 days, today in `chart`, history `chart-muted`, `role="img"` + text summary) + Providers list (status badge, p95) → Spend by department table.
- **Keys:** header with count summary + New key → search + status segmented (with counts) → table (Key/prefix, Owner team·department, Status, Access models·residency, Last used). Row → detail drawer (Owner, Access, Limits, Lifecycle; Rotate, Edit, Revoke). Rotate dialog: radio cards *Revoke old key now* (default) / *Keep old key for 24 hours*, note "Budgets and spend carry over". Then secret reveal.
- **New key drawer:** fieldsets Basics (name, team), Access (residency chips, PII segmented + explanation, allow-all-models checkbox → model checklist), Limits (rpm, tpm, expiry). Validate on submit, focus first invalid field, map ProblemDetails `errors` by field.
- **Organisation:** master list of departments (name, mono ansvarskod, team count) → detail card (name, cost center, budget meter) + Teams table. Delete is `aria-disabled` with reason when a 409 would occur (has teams / has keys).
- **Routes:** master list (mono name, kind, target count, warning icon if any target unhealthy) → Fallback chain: priority tiers left→right joined by "on failure" arrows; each target tile shows model (mono), provider, residency, health badge, weight share bar. Below: "Where requests land" table for Any / EU only / On-prem only keys; show `no_eligible_provider` in danger when nothing qualifies. Note: never falls back after streaming has started.
- **Budgets & alerts:** Open alerts (tinted rows, Acknowledge) → scope segmented → table (Owner+scope, Period, meter with threshold ticks + %, Spent / limit, Resets). Footnote: estimates, not invoices.
- **Role awareness:** hide nav sections the role can't use (`viewer`: Overview, Usage, Catalogue; `department-admin`: + Keys, Organisation, Budgets). Never show buttons that will 403.

## 7. Content style
- Sentence case, plain English, active voice, no "please", no exclamation marks. Numbers: Swedish formatting (`48 210 kr`, NBSP thousands), dates `8 Oct 2026`, times 24 h Europe/Stockholm, relative times for < 7 days ("2 min ago").
- Money is SEK with `kr` suffix; USD only on price configuration fields, labelled "USD per 1M tokens".
- Domain terms: Department (förvaltning), Cost center (ansvarskod), Team, Key (not "virtual key" in buttons), Route, Provider, Residency.
- Errors: say what happened + what to do. "Give the key a name so people can recognise it."

## 8. Accessibility checklist (blocks release)
- Contrast ≥4.5:1 text, ≥3:1 UI boundaries & focus indicator (already in tokens).
- Focus ring: global `:focus-visible` 2 px `focus` + 2 px offset; never remove. Sticky top bar must not obscure focused items (WCAG 2.4.11 — add `scroll-padding-top: 76px`).
- Target size ≥24×24 (2.5.8); we use ≥32 desktop, ≥44 mobile.
- Landmarks: `nav[aria-label=Main]`, `main#main`, skip link "Skip to content" first in DOM. One `h1` per page; headings in order.
- Tables: `caption` (visually hidden ok), `th scope`. Charts: `role="img"` + `aria-label` summary, and a "View as table" toggle.
- Dialogs/drawers: `role="dialog" aria-modal="true" aria-labelledby`, focus first field on open, trap Tab, Esc closes (except unconfirmed secret), return focus to trigger.
- Live regions: toasts in `role="status"`; form error summary `role="alert"`; session-expiry warning 5 min before with "Stay signed in" (POST `/bff/session/extend`) — must be keyboard reachable and not time out under 20 s (WCAG 2.2.1).
- Status not by color alone (dot + word; residency glyph + word). `prefers-reduced-motion` honoured (tokens.css). `forced-colors` honoured — badges get `border: 1px solid CanvasText`.
- Text resize to 200% and reflow at 320 px without horizontal scroll (tables → cards).
- `lang="en"` on `html`; `lang="sv"` on Swedish names and Swedish API messages.

## 9. Keyboard flows (document & test each)
| Task | Keys |
|---|---|
| Global search | `⌘K` / `Ctrl K` opens; type; `↑↓` select; `Enter` open; `Esc` close |
| Create key | Keys page → `Tab` to *New key* → `Enter` → fields in order → `Enter` on *Create key* → secret dialog: `Tab` to Copy `Enter`, `Space` on checkbox, `Enter` on Done → focus returns to new row |
| Rotate key | Row button `Enter` → drawer → *Rotate* `Enter` → `↑↓` choose mode → `Tab` *Rotate key* `Enter` → secret flow |
| Revoke key | Drawer → `Tab` to *Revoke* `Enter` → dialog focus on *Cancel* (safe default) → `Tab` *Revoke key* `Enter` → toast announced, focus to drawer title |
| Acknowledge alert | `Tab` through alert rows; `Enter` on *Acknowledge*; focus moves to next alert (or heading if none) |
| Close/open circuit | Overview attention card or Operations row → `Enter` → confirm dialog for *Open* only |
| Filters | Segmented items are buttons in tab order; `Enter`/`Space` toggles |
| Master/detail | List is a `nav` of buttons; `Enter` loads detail and moves focus to its heading |

## 10. API → UI mapping notes
- Status label maps in §5. Enum strings come as `"InGracePeriod"` etc. — never display raw enums.
- `allowedModels: []` → "All models"; `allowedResidencies: []` → "Any residency".
- 409 messages arrive in Swedish; prefer pre-empting with disabled-with-reason, otherwise show inline with `lang="sv"`.
- 429 → inline banner with countdown from `Retry-After`. 401 → session-expired dialog with sign-in (keep unsaved form in memory).
- `lastWriteAt=null` → "No writes yet"; provider p95 `null` → "—" with tooltip "No traffic in 24 h" (not 0).
