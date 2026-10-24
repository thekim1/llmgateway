// Builds the static website into site/dist: the product page, Getting started, Benchmarks and every Markdown page in
// content.mjs DOCS rendered as documentation. No framework; one dependency (marked).
//
//   node build.mjs                 build for https://<host>/        (SITE_BASE=/llmgateway/ for GitHub Pages)
//   node build.mjs --serve         build, then serve dist on http://localhost:4321
//
// The build fails on a link to a documentation page or heading that does not exist.

import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:http'
import { dirname, extname, join, posix, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { Marked } from 'marked'
import * as C from './content.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, '..')
const dist = join(here, 'dist')
const base = (process.env.SITE_BASE ?? '/').replace(/\/?$/, '/')
const origin = process.env.SITE_ORIGIN ?? ''
const errors = []
const icons = new Set(['arrow_forward', 'check', 'check_circle', 'close', 'content_copy', 'dark_mode', 'description', 'edit', 'info', 'light_mode', 'menu', 'search', 'warning', 'auto_awesome', 'open_in_new'])

const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
const url = p => base + p.replace(/^\//, '')
const icon = (name, cls = '') => { icons.add(name); return `<span class="ms${cls ? ' ' + cls : ''}" aria-hidden="true">${name}</span>` }
const nf = n => n.toLocaleString('sv-SE').replace(/ /g, ' ')

// ---------- Markdown ----------

const docs = C.DOCS.flatMap(g => g.items.map(d => ({ ...d, group: g.group, href: url(d.slug ? `docs/${d.slug}/` : 'docs/') })))
const bySource = new Map(docs.map(d => [d.source, d]))

/** GitHub's heading anchors, so links written for the repository keep working on the site. */
function slugger() {
  const seen = new Map()
  return text => {
    let slug = text.toLowerCase().replace(/<[^>]+>/g, '').replace(/[^\p{L}\p{N}\s_-]/gu, '').trim().replace(/\s/g, '-')
    const n = seen.get(slug) ?? 0
    seen.set(slug, n + 1)
    return n ? `${slug}-${n}` : slug
  }
}

function stripTags(html) { return html.replace(/<[^>]+>/g, '').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'") }

const anchors = new Map() // source -> Set of heading ids, filled before links are checked
const pendingLinks = [] // [fromSource, targetSource, hash]

/** Rewrites a link in a Markdown file to the site page, a copied asset or the file on GitHub. */
function resolveLink(href, fromSource) {
  if (/^([a-z]+:|#|\/\/)/i.test(href)) return href
  const [path, hash = ''] = href.split('#')
  const target = posix.normalize(posix.join(posix.dirname(fromSource), decodeURI(path)))
  const doc = bySource.get(target)
  if (doc) { pendingLinks.push([fromSource, target, hash]); return doc.href + (hash ? `#${hash}` : '') }
  if (C.DOC_ASSETS.some(a => target.startsWith(a + '/'))) return url(target.replace(/^docs\//, ''))
  if (target.startsWith('..')) { errors.push(`${fromSource}: link outside the repository: ${href}`); return href }
  if (!existsSync(join(repo, target))) errors.push(`${fromSource}: link to a missing file: ${href}`)
  return `${C.GH}/blob/main/${target}${hash ? `#${hash}` : ''}`
}

function renderMarkdown(source) {
  const slug = slugger()
  const toc = []
  const ids = new Set()
  const marked = new Marked({ gfm: true })
  marked.use({
    renderer: {
      heading({ tokens, depth }) {
        const html = this.parser.parseInline(tokens)
        const id = slug(stripTags(html))
        ids.add(id)
        if (depth === 1) return '' // the page header shows the title
        if (depth === 2) toc.push({ id, label: stripTags(html) })
        return `<h${depth} id="${id}"><a class="anchor" href="#${id}" aria-hidden="true" tabindex="-1">#</a>${html}</h${depth}>\n`
      },
      code({ text, lang }) {
        const label = { powershell: 'PowerShell', bash: 'bash', sh: 'shell', python: 'Python', json: 'JSON', csharp: 'C#', yaml: 'YAML', sql: 'SQL', http: 'HTTP', typescript: 'TypeScript', js: 'JavaScript', text: 'Text' }[lang] ?? (lang || 'Code')
        return codeBlock(text, label)
      },
      link({ href, title, tokens }) {
        const to = resolveLink(href, source)
        const external = /^https?:/.test(to) && !(origin && to.startsWith(origin))
        return `<a href="${esc(to)}"${title ? ` title="${esc(title)}"` : ''}${external ? ' rel="noopener"' : ''}>${this.parser.parseInline(tokens)}</a>`
      },
      image({ href, title, text }) {
        return `<img src="${esc(resolveLink(href, source))}" alt="${esc(text)}"${title ? ` title="${esc(title)}"` : ''} loading="lazy">`
      },
      blockquote({ tokens }) { return `<div class="callout" role="note">${icon('info')}<div>${this.parser.parse(tokens)}</div></div>\n` },
      table(token) {
        // marked's default table, inside a scrolling card.
        const cell = (c, tag) => `<${tag}${c.align ? ` style="text-align:${c.align}"` : ''}${tag === 'th' && c.header ? ' scope="col"' : ''}>${this.parser.parseInline(c.tokens)}</${tag}>`
        const head = `<tr>${token.header.map(c => cell(c, 'th')).join('')}</tr>`
        const body = token.rows.map(r => `<tr>${r.map(c => cell(c, 'td')).join('')}</tr>`).join('\n')
        return `<div class="table-card"><table><thead>${head}</thead><tbody>${body}</tbody></table></div>\n`
      },
    },
  })
  const md = readFileSync(join(repo, source), 'utf8').replace(/^﻿/, '')
  const html = marked.parse(md)
  anchors.set(source, ids)
  return { html, toc }
}

function codeBlock(code, label, extra = '') {
  return `<div class="code"${extra}><div class="code-bar"><span>${esc(label)}</span><button type="button" class="copy" data-copy>${icon('content_copy')}<span>Copy</span></button></div><pre><code>${esc(code.replace(/\n$/, ''))}</code></pre></div>\n`
}

// ---------- Layout ----------

const NAV = [['home', 'Product', ''], ['start', 'Getting started', 'start/'], ['docs', 'Docs', 'docs/'], ['bench', 'Benchmarks', 'benchmarks/']]
const THEMES = [['light', 'Light', 'light_mode'], ['dark', 'Dark', 'dark_mode'], ['lumen', 'Lumen', 'auto_awesome']]
const ghMark = '<svg aria-hidden="true" width="16" height="16" viewBox="0 0 16 16" fill="currentColor"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"></path></svg>'

function layout({ page, title, description, path, body }) {
  const nav = NAV.map(([id, label, href]) => `<a href="${url(href)}"${id === page ? ' aria-current="page"' : ''}>${label}</a>`).join('')
  const footer = [
    ['Product', [['Getting started', url('start/')], ['Benchmarks', url('benchmarks/')], ['Changelog', url('docs/changelog/')]]],
    ['Docs', [['Routing rules', url('docs/routing-rules/')], ['Admin API', url('docs/admin-api/')], ['Runbook', url('docs/runbook/')]]],
    ['Trust', [['Security and compliance', url('docs/security-and-compliance/')], ['Accessibility statement', url('docs/accessibility-statement/')], ['GitHub', C.GH]]],
  ]
  const fullTitle = page === 'home' ? 'Ume AI Gateway' : `${title} · Ume AI Gateway`
  return `<!doctype html>
<html lang="en" data-theme="light">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(fullTitle)}</title>
<meta name="description" content="${esc(description)}">
<meta property="og:title" content="${esc(fullTitle)}">
<meta property="og:description" content="${esc(description)}">
${origin ? `<link rel="canonical" href="${esc(origin + url(path))}">\n` : ''}<link rel="icon" href="${url('assets/favicon.svg')}" type="image/svg+xml">
<script>try{var t=localStorage.getItem('ume-site-theme');document.documentElement.dataset.theme=t||(matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light')}catch(e){}</script>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Geist:wght@400;500;600;700&family=Geist+Mono:wght@400;500&display=swap">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Material+Symbols+Rounded:opsz,wght,GRAD@20..24,400,0&icon_names=__ICONS__&display=block">
<link rel="stylesheet" href="${url('assets/tokens.css')}">
<link rel="stylesheet" href="${url('assets/site.css')}">
<script src="${url('assets/site.js')}" defer></script>
</head>
<body>
<a class="skip" href="#main">Skip to content</a>
<header class="top">
  <div class="top-in">
    <a class="brand" href="${url('')}"><span class="logo" aria-hidden="true">U</span><span>Ume AI Gateway</span></a>
    <nav class="nav-main" aria-label="Main">${nav}</nav>
    <div class="spacer"></div>
    <div class="seg theme-top" role="group" aria-label="Theme">${THEMES.map(([id, label, ic]) => `<button type="button" data-theme-set="${id}" aria-pressed="false" aria-label="${label} theme" title="${label}">${icon(ic)}</button>`).join('')}</div>
    <a class="btn-dark gh" href="${C.GH}">${ghMark}GitHub</a>
    <button type="button" class="menu-btn" aria-expanded="false" aria-controls="mnav" aria-label="Menu">${icon('menu')}</button>
  </div>
  <div id="mnav" class="nav-mobile" hidden>
    <nav aria-label="Main">${nav}<a href="${C.GH}">GitHub</a></nav>
    <div class="seg theme-menu" role="group" aria-label="Theme">${THEMES.map(([id, label, ic]) => `<button type="button" data-theme-set="${id}" aria-pressed="false">${icon(ic)}${label}</button>`).join('')}</div>
  </div>
</header>
<main id="main">
${body}
</main>
<footer class="foot">
  <div class="foot-in">
    <div class="foot-about"><span class="b">Ume AI Gateway</span><span>A proof of concept by Umeå kommun. Not a production approval, legal compliance attestation or WCAG certification.</span></div>
    ${footer.map(([t, links]) => `<div class="foot-col"><span class="b">${t}</span>${links.map(([l, h]) => `<a href="${h}">${l}</a>`).join('')}</div>`).join('\n    ')}
  </div>
</footer>
<div class="toast-region" role="status" aria-live="polite"></div>
</body>
</html>
`
}

/** A screenshot in each theme; CSS shows the one that matches the page's theme. */
function shot(name, alt, cls = '') {
  const themed = ['light', 'dark', 'lumen'].filter(t => existsSync(join(here, 'public', 'screenshots', `${name}-${t}.png`)))
  if (!themed.length) {
    errors.push(`missing screenshot: public/screenshots/${name}-light.png (run the screenshot script)`)
    return `<div class="shot-missing ${cls}" role="img" aria-label="${esc(alt)}"><code>${esc(name)}</code></div>`
  }
  return `<div class="shots ${cls}">${themed.map(t => `<img class="shot shot-${t}${themed.length === 1 ? ' shot-any' : ''}" src="${url(`screenshots/${name}-${t}.png`)}" alt="${esc(alt)}" loading="lazy" decoding="async">`).join('')}</div>`
}

const eyebrow = t => `<span class="eyebrow">${t}</span>`

// ---------- Pages ----------

function homePage() {
  const sdkTabs = C.SDK.map((s, i) => `<button type="button" role="tab" id="sdk-tab-${s.id}" aria-controls="sdk-${s.id}" aria-selected="${i === 0}" tabindex="${i === 0 ? 0 : -1}">${s.label}</button>`).join('')
  const sdkPanels = C.SDK.map((s, i) => `<pre role="tabpanel" id="sdk-${s.id}" aria-labelledby="sdk-tab-${s.id}" tabindex="0"${i ? ' hidden' : ''}><code>${esc(s.code)}</code></pre>`).join('')
  return `
<section class="hero wrap" aria-labelledby="hero-h">
  <span class="pill"><span class="dot" aria-hidden="true"></span>Open source · .NET 10 · Built in Umeå</span>
  <h1 id="hero-h">One gateway between your organisation and every model.</h1>
  <p class="lead">Virtual keys, budgets, routing that keeps personal data on-prem, and fallback when a provider goes down. The gateway stores usage metadata only, never prompts or responses.</p>
  <div class="actions"><a class="btn btn-primary" href="${url('start/')}">Get started${icon('arrow_forward')}</a><a class="btn btn-ghost" href="${url('docs/')}">Read the docs</a></div>
</section>
<div class="wrap pt-64"><div class="frame frame-lg">${shot('overview-desktop', 'The admin UI overview: needs attention, key figures, daily spend and provider health', 'r-20')}</div></div>
<section class="wrap pt-80" aria-label="Performance at a glance">
  <div class="proof">${C.PROOF.map(p => `<div><span class="big">${p.value}</span><span class="b">${p.label}</span><span class="sub">${p.sub}</span></div>`).join('')}</div>
  <p class="fine">Measured with an instant fake LLM on a 16-thread laptop, October 2026. <a href="${url('benchmarks/')}">How we measure</a></p>
</section>
<section class="wrap pt-120 stack-40" aria-labelledby="pillars-h">
  <div class="intro">${eyebrow('Made for the public sector')}<h2 id="pillars-h" class="h-xl">Control over AI that you can explain to a data protection officer.</h2></div>
  <div class="grid-280">${C.PILLARS.map(p => `<article class="card pillar"><span class="tile tone-${p.tone}" aria-hidden="true">${icon(p.icon, 'big-icon')}</span><h3>${p.title}</h3><p>${p.body}</p><ul>${p.points.map(pt => `<li>${icon('check', 'ok')}<span>${pt}</span></li>`).join('')}</ul></article>`).join('')}</div>
</section>
<section class="wrap pt-120" aria-labelledby="rules-h">
  <div class="split">
    <div class="stack-16">${eyebrow('Routing rules')}<h2 id="rules-h" class="h-l">Apps ask for a model. Rules decide where it goes.</h2>
      <p class="body-l">Write a condition on headers, team, budget use or personal data, then send matching requests to weighted targets with fallbacks. Test a sample request before you rely on it. The response header <code>x-ume-rule</code> tells you which rule applied.</p>
      <div class="term">pii_detected &amp;&amp; endpoint == "chat_completions"
<span class="mute">→ send to</span> ume/chat-onprem   <span class="mute">· no fallbacks, on purpose</span></div>
      <a class="more" href="${url('docs/routing-rules/')}">Read the routing guide${icon('arrow_forward')}</a>
    </div>
    <figure class="frame"><img src="${url('images/routing-pii-rule.png')}" alt="The routing rules page with the rule Personal data stays on-prem at the top of the list" loading="lazy"></figure>
  </div>
</section>
<section class="wrap pt-120" aria-labelledby="sdk-h">
  <div class="split">
    <div class="stack-16 order-2">${eyebrow('Drop-in compatible')}<h2 id="sdk-h" class="h-l">Keep the SDKs your teams already use.</h2>
      <p class="body-l">Point the OpenAI or Anthropic SDK at the gateway and swap in a virtual key. Chat, Responses, Messages, embeddings, speech to text, images and live audio all go through one pipeline: key, limits, rules, residency, budget, fallback.</p>
      <div class="chips">${C.ENDPOINTS.map(e => `<span class="chip mono">${e}</span>`).join('')}</div>
    </div>
    <div class="sdk order-1" data-tabs>
      <div class="sdk-bar"><div role="tablist" aria-label="Language">${sdkTabs}</div><button type="button" class="copy" data-copy-active>${icon('content_copy')}<span>Copy</span></button></div>
      ${sdkPanels}
    </div>
  </div>
</section>
<section class="wrap pt-120 stack-40" aria-labelledby="ui-h">
  <div class="intro">${eyebrow('Admin UI')}<h2 id="ui-h" class="h-l">Built for the people who run it, in three themes.</h2>
    <p class="body-l">Departments, teams, keys, budgets and alerts in one place. Every release is tested with axe against WCAG 2.2 AA in light, dark and Lumen, plus keyboard, reflow, forced colours and target size.</p></div>
  <div class="grid-340">${C.UI_SHOTS.map(s => `<figure class="ui-shot"><div class="frame frame-sm">${shot(s.file, s.alt, 'r-15 ratio-43')}</div><figcaption><span class="b">${s.title}</span><span>${s.body}</span></figcaption></figure>`).join('')}</div>
</section>
<section class="wrap pt-120 stack-40" aria-labelledby="deploy-h">
  <div class="intro">${eyebrow('Deploy')}<h2 id="deploy-h" class="h-l">Runs on your own servers, behind your own login.</h2></div>
  <div class="grid-260">${C.DEPLOYS.map(d => `<a class="card link-card" href="${url(d.href)}">${icon(d.icon, 'accent big-icon')}<span class="t">${d.title}</span><span class="d">${d.body}</span><span class="m mono">${d.meta}</span></a>`).join('')}</div>
</section>
<section class="wrap pt-120" aria-labelledby="cta-h">
  <div class="cta"><h2 id="cta-h">Running in about ten minutes on a laptop with Docker.</h2>
    <p>Demo data, a fake LLM and test users are included. It's a demonstrator, not a production approval, legal compliance attestation or WCAG certification.</p>
    <div class="actions"><a class="btn btn-inverse" href="${url('start/')}">Get started</a><a class="btn btn-outline" href="${C.GH}">View on GitHub</a></div></div>
</section>`
}

function startPage() {
  const tabs = C.PATHS.map((p, i) => `<button type="button" role="tab" id="path-tab-${p.id}" aria-controls="path-${p.id}" aria-selected="${i === 0}" tabindex="${i === 0 ? 0 : -1}" data-hash="${p.id}" class="path"><span class="tile" aria-hidden="true">${icon(p.icon)}</span><span class="txt"><span class="t">${p.title}</span><span class="d">${p.body}</span></span></button>`).join('')
  const panels = C.PATHS.map((p, i) => `<div role="tabpanel" id="path-${p.id}" aria-labelledby="path-tab-${p.id}" class="stack-40"${i ? ' hidden' : ''}>
    <div class="card needs"><h2>You need</h2><ul>${p.prereqs.map(r => `<li>${icon('check_circle', 'ok')}${r}</li>`).join('')}</ul></div>
    <ol class="steps">${p.steps.map((st, n) => `<li><div class="rail"><span class="step-num" aria-hidden="true">${n + 1}</span><span class="line" aria-hidden="true"></span></div><div class="step">
      <h2><span class="sr">Step ${n + 1}: </span>${st.title}</h2><p>${esc(st.body)}</p>
      ${st.code ? codeBlock(st.code, st.shell) : ''}${st.shot ? `<div class="frame frame-sm">${shot(st.shot, st.shotAlt, 'r-15')}</div>` : ''}${st.note ? `<p class="note">${icon('info', 'fill accent')}<span>${esc(st.note)}</span></p>` : ''}
    </div></li>`).join('')}</ol></div>`).join('\n')
  const next = [
    { title: 'Routing rules', body: 'Keep personal data on-prem with one rule.', href: url('docs/routing-rules/') },
    { title: 'Benchmarks', body: 'What the gateway adds to each request.', href: url('benchmarks/') },
    { title: 'Runbook', body: 'Backups, certificates and incidents.', href: url('docs/runbook/') },
  ]
  return `
<div class="wrap-960 pt-64 stack-40">
  <header class="stack-12">${eyebrow('Getting started')}<h1 class="h-page">From zero to your first request.</h1><p class="lead-s">Try it on your own machine with demo data, or set up a production host. You can switch paths at any time.</p></header>
  <div data-tabs class="stack-40"><div role="tablist" aria-label="Path" class="paths">${tabs}</div>${panels}</div>

  <section class="stack-16" aria-labelledby="connect-h">
    <h2 id="connect-h" class="h-m">Connect an application</h2>
    <p>Applications need two things from an administrator: a <strong>key</strong> from their team and the gateway's <strong>base URL</strong> (<code>https://&lt;gateway&gt;/v1</code>). Read the key from an environment variable such as <code>UME_API_KEY</code>; never store it in code. Ask for an alias from the admin UI's <em>Catalogue</em>, such as <code>ume/chat-standard</code>, rather than a vendor model. The gateway publishes its OpenAPI document at <code>/openapi/v1.json</code> and an API reference at <code>/scalar</code>.</p>
    <p class="note warn">${icon('warning', 'fill warn')}<span>Prompts and responses are never saved by the gateway. Don't send confidential information to external providers without an approved assessment.</span></p>
    <div class="grid-260">${[
      ['Chat, Responses, Messages', 'OpenAI and Anthropic SDKs, unchanged.', 'docs/admin-api/#gateway-data-plane--for-the-developer-portal-snippets'],
      ['Speech to text', 'Upload a recording to /v1/audio/transcriptions.', 'docs/speech-to-text/'],
      ['Live audio', 'Realtime sessions and interpreting over WebSockets, from your backend.', 'docs/live-audio/'],
      ['Images', 'Generate and edit pictures at /v1/images.', 'docs/image-generation/'],
      ['Attached files', 'Images, PDFs and audio inline in the request.', 'docs/attached-files/'],
    ].map(([t, d, h]) => `<a class="card link-card small" href="${url(h)}"><span class="t">${t}${icon('arrow_forward')}</span><span class="d">${d}</span></a>`).join('')}</div>
  </section>

  <section class="stack-16" aria-labelledby="errors-h">
    <h2 id="errors-h" class="h-m">Common errors</h2>
    <p>Errors use the OpenAI error shape with a stable <code>code</code>. To investigate a failed call, find it by request ID under <em>Monitor › Usage › Requests and costs</em> in the admin UI.</p>
    <div class="table-card"><table><thead><tr><th scope="col">Code</th><th scope="col">Meaning</th></tr></thead><tbody>${C.ERROR_CODES.map(([c, m]) => `<tr><td><code>${c}</code></td><td>${esc(m)}</td></tr>`).join('')}</tbody></table></div>
  </section>

  <section class="stack-16" aria-labelledby="terms-h">
    <h2 id="terms-h" class="h-m">Terms</h2>
    <dl class="card terms">${C.GLOSSARY.map(([t, d]) => `<div><dt>${t}</dt><dd>${esc(d)}</dd></div>`).join('')}</dl>
  </section>

  <div class="grid-260">${next.map(l => `<a class="card link-card small" href="${l.href}"><span class="k">Next</span><span class="t">${l.title}${icon('arrow_forward')}</span><span class="d">${l.body}</span></a>`).join('')}</div>
</div>`
}

function benchPage() {
  const chart = C.SCALE.map((sc, i) => {
    const max = Math.max(...sc.a, ...sc.b) * 1.12
    const pct = v => (v / max * 100).toFixed(1) + '%'
    const alt = `Messages per second by ${sc.label.toLowerCase()}. ` + sc.x.map((x, j) => `${x}: ${nf(sc.a[j])} on 9 October, ${nf(sc.b[j])} on 15 October`).join('; ') + '.'
    return `<div role="tabpanel" id="chart-${sc.id}" aria-labelledby="chart-tab-${sc.id}"${i ? ' hidden' : ''}>
      <div class="bars" role="img" aria-label="${esc(alt)}" style="--n:${sc.x.length}">${sc.x.map((x, j) => `<div class="bar-g"><div class="bar a" style="height:${pct(sc.a[j])}" title="9 Oct: ${nf(sc.a[j])} msg/s"></div><div class="bar b" style="height:${pct(sc.b[j])}" title="15 Oct: ${nf(sc.b[j])} msg/s"><span>${nf(sc.b[j])}</span></div></div>`).join('')}</div>
      <div class="bar-x" style="--n:${sc.x.length}" aria-hidden="true">${sc.x.map(x => `<span>${x}</span>`).join('')}</div>
      <p class="axis" aria-hidden="true">${sc.label}</p>
    </div>`
  }).join('')
  return `
<div class="wrap-1100 pt-64 stack-56">
  <header class="stack-12 measure">${eyebrow('Benchmarks')}<h1 class="h-page">Close to zero added latency.</h1><p class="lead-s">The gateway sits on the path of every LLM call, so it's built to add as little as possible. These runs use a fake LLM that answers instantly, so only the gateway's own work is measured.</p></header>
  <div class="grid-220">${C.BENCH_STATS.map(b => `<div class="card stat"><span class="l">${b.label}</span><span class="v">${b.value}</span><span class="s">${b.sub}</span></div>`).join('')}</div>

  <section class="card panel stack-20" aria-labelledby="scale-h" data-tabs>
    <div class="panel-head"><div class="stack-4"><h2 id="scale-h" class="h-s">Throughput as load grows</h2><span class="muted">Streaming chat, messages per second, 4 vCPUs unless noted</span></div>
      <div class="seg" role="tablist" aria-label="Chart">${C.SCALE.map((sc, i) => `<button type="button" role="tab" id="chart-tab-${sc.id}" aria-controls="chart-${sc.id}" aria-selected="${i === 0}" tabindex="${i === 0 ? 0 : -1}">${sc.tab}</button>`).join('')}</div></div>
    <div class="legend"><span><i class="sw a"></i>9 Oct 2026</span><span><i class="sw b"></i>15 Oct 2026 (COPY writer)</span></div>
    ${chart}
  </section>

  <section class="stack-20" aria-labelledby="cost-h">
    <div class="stack-6 measure"><h2 id="cost-h" class="h-m">What one request costs</h2><p class="muted">With warm caches, a request makes no Postgres calls on the request path. Keys and the routing catalogue live in memory; usage is written in batches in the background.</p></div>
    <div class="table-card"><table><thead><tr><th scope="col">Step</th><th scope="col">When</th><th scope="col" class="num">Redis round trips</th><th scope="col">How</th></tr></thead><tbody>${C.COST_ROWS.map(c => `<tr><th scope="row">${c.step}</th><td class="nowrap muted">${c.when}</td><td class="num b">${c.rt}</td><td class="muted">${esc(c.how)}</td></tr>`).join('')}</tbody></table></div>
  </section>

  <section class="stack-20" aria-labelledby="cmp-h">
    <div class="stack-6 measure"><h2 id="cmp-h" class="h-m">Next to eneo, on the same fake LLM</h2><p class="muted">Same four pinned cores for each platform. Both are called the way an external application calls them: the gateway with a virtual key, eneo with a service API key. These are different kinds of product: eneo is a chat application that stores every conversation; the gateway is a stateless proxy. The numbers show what each adds on top of the model call, not which is better.</p></div>
    <div class="card panel stack-24">${C.COMPARE.map(c => `<div class="stack-10"><span class="b">${c.label}</span>${c.rows.map(([n, v, l], j) => `<div class="hbar"><span class="muted">${n}</span><div class="track"><div class="fill${j ? ' other' : ''}" style="width:${Math.max(0.6, v / c.max * 100).toFixed(2)}%"></div></div><span class="b num">${l}</span></div>`).join('')}</div>`).join('')}
      <span class="fine">Lower is better. Measured 20 Oct 2026, streaming, 30 s per point, 0 failed checks. Peak on the same four cores: 16 180 messages/s for the gateway, 49 for eneo.</span></div>
  </section>

  <section class="stack-20" aria-labelledby="reports-h">
    <h2 id="reports-h" class="h-m">Full results</h2>
    <div class="grid-240">${C.REPORTS.map(r => `<a class="card link-card small" href="${url(r.href)}"><span class="t">${r.title}${icon('arrow_forward')}</span><span class="d">${r.body}</span>${r.meta ? `<span class="m mono">${r.meta}</span>` : ''}</a>`).join('')}</div>
    <p class="fine">The platform comparison is also a <a href="${url('reports/platform-comparison.pdf')}">PDF</a>.</p>
  </section>

  <section class="split-380" aria-labelledby="run-h">
    <div class="stack-10"><h2 id="run-h" class="h-m">Run it yourself</h2><p class="muted">Absolute numbers depend on the machine; compare runs on the same host. Postgres and Redis ran in Docker on WSL2 here, so Redis round trips cost more than on a production network.</p>
      <a class="more" href="${url('docs/performance/#measuring')}">How to measure${icon('arrow_forward')}</a></div>
    ${codeBlock(C.BENCH_CODE, 'PowerShell · needs Docker, Release build')}
  </section>
</div>`
}

function docPage(d, rendered) {
  const i = docs.indexOf(d)
  const prev = docs[i - 1], next = docs[i + 1]
  const links = C.DOCS.map(g => `<div class="side-g"><span class="side-h">${g.group}</span>${g.items.map(x => { const dx = docs.find(y => y.source === x.source); return `<a href="${dx.href}"${dx === d ? ' aria-current="page"' : ''}>${x.title}</a>` }).join('')}</div>`).join('')
  // Phones and tablets get the same menu and "On this page" as collapsibles above the page, so they work without JavaScript.
  const menu = `<details class="doc-menu"><summary><span class="k">${d.group}</span><span class="b">${esc(d.title)}</span>${icon('expand_more', 'chev')}<span class="sr"> – all documentation pages</span></summary><nav aria-label="Documentation">${links}</nav></details>`
  const toc = rendered.toc.length > 1 ? `<aside class="toc" aria-label="On this page"><span class="b">On this page</span>${rendered.toc.map(t => `<a href="#${t.id}">${esc(t.label)}</a>`).join('')}</aside>` : ''
  const tocInline = toc ? `<details class="toc-inline"><summary>On this page${icon('expand_more', 'chev')}</summary><nav aria-label="On this page">${rendered.toc.map(t => `<a href="#${t.id}">${esc(t.label)}</a>`).join('')}</nav></details>` : ''
  return `
<div class="docs${toc ? ' has-toc' : ''}">
  <nav class="side" aria-label="Documentation">
    <label class="search">${icon('search')}<span class="sr">Search documentation</span><input type="search" placeholder="Filter pages" data-doc-filter></label>
    ${links}
  </nav>
  <article class="doc">
    ${menu}
    <nav aria-label="Breadcrumb" class="crumbs"><a href="${url('docs/')}">Docs</a><span aria-hidden="true">/</span><span>${d.group}</span></nav>
    <h1>${esc(d.title)}</h1>
    <p class="doc-lead">${esc(d.lead)}</p>
    <div class="doc-meta"><span>${icon('description')}${d.source}</span><a href="${C.GH}/edit/main/${d.source}">${icon('edit')}Edit on GitHub</a></div>
    ${tocInline}
    <div class="md">${rendered.html}</div>
    <nav aria-label="Previous and next" class="pager">${prev ? `<a href="${prev.href}"><span class="k">Previous</span><span class="b">${esc(prev.title)}</span></a>` : '<span></span>'}${next ? `<a class="nx" href="${next.href}"><span class="k">Next</span><span class="b">${esc(next.title)}</span></a>` : ''}</nav>
  </article>
  ${toc}
</div>`
}

// ---------- Build ----------

rmSync(dist, { recursive: true, force: true })
mkdirSync(dist, { recursive: true })
const out = []
const write = (path, html) => out.push([path, html])

write('index.html', layout({ page: 'home', title: 'Ume AI Gateway', path: '', description: 'An open-source LLM gateway for the public sector: virtual keys, budgets, routing rules that keep personal data on-prem, and provider fallback.', body: homePage() }))
write('start/index.html', layout({ page: 'start', title: 'Getting started', path: 'start/', description: 'Run the Ume AI Gateway locally with demo data, or deploy a release to production, and send your first request.', body: startPage() }))
write('benchmarks/index.html', layout({ page: 'bench', title: 'Benchmarks', path: 'benchmarks/', description: 'What the Ume AI Gateway adds to each LLM request: latency, throughput, CPU and a comparison with eneo.', body: benchPage() }))
const rendered = new Map(docs.map(d => [d, renderMarkdown(d.source)]))
for (const d of docs) write(`${d.slug ? `docs/${d.slug}/` : 'docs/'}index.html`, layout({ page: 'docs', title: d.title, path: d.slug ? `docs/${d.slug}/` : 'docs/', description: d.lead, body: docPage(d, rendered.get(d)) }))
write('404.html', layout({ page: '', title: 'Page not found', path: '404.html', description: 'Page not found.', body: `<div class="wrap-960 pt-64 stack-16"><h1 class="h-page">Page not found.</h1><p class="lead-s">The page may have moved. Try the <a href="${url('docs/')}">documentation</a> or the <a href="${url('')}">home page</a>.</p></div>` }))

for (const [from, target, hash] of pendingLinks) {
  if (hash && !anchors.get(target)?.has(hash)) errors.push(`${from}: no heading #${hash} in ${target}`)
}
for (const href of [...C.DEPLOYS.map(d => d.href), 'docs/admin-api/#gateway-data-plane--for-the-developer-portal-snippets', 'docs/performance/#measuring']) {
  const [p, hash] = href.split('#')
  const d = docs.find(x => x.href === url(p))
  if (!d) errors.push(`content.mjs: no page ${p}`)
  else if (hash && !anchors.get(d.source).has(hash)) errors.push(`content.mjs: no heading #${hash} in ${d.source}`)
}

const iconParam = [...icons].sort().join(',')
for (const [path, html] of out) {
  mkdirSync(dirname(join(dist, path)), { recursive: true })
  writeFileSync(join(dist, path), html.replace('__ICONS__', iconParam))
}
cpSync(join(here, 'assets'), join(dist, 'assets'), { recursive: true })
cpSync(join(here, 'public'), dist, { recursive: true })
cpSync(join(repo, 'src/admin-ui/src/styles/tokens.css'), join(dist, 'assets/tokens.css')) // the admin UI's tokens, so both look the same
for (const a of C.DOC_ASSETS) cpSync(join(repo, a), join(dist, a.replace(/^docs\//, '')), { recursive: true })
writeFileSync(join(dist, '.nojekyll'), '')

if (errors.length) {
  console.error(`\n${errors.length} problem(s):\n  ` + [...new Set(errors)].join('\n  '))
  process.exitCode = 1
} else console.log(`Built ${out.length} pages into ${relative(process.cwd(), dist) || '.'} (base ${base})`)

if (process.argv.includes('--serve')) {
  const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png', '.pdf': 'application/pdf' }
  createServer((req, res) => {
    let p = decodeURIComponent(new URL(req.url, 'http://x').pathname)
    if (!p.startsWith(base)) { res.writeHead(302, { location: base }); return res.end() }
    p = join(dist, p.slice(base.length))
    if (existsSync(p) && statSync(p).isDirectory()) p = join(p, 'index.html')
    if (!existsSync(p)) { res.writeHead(404, { 'content-type': types['.html'] }); return res.end(readFileSync(join(dist, '404.html'))) }
    res.writeHead(200, { 'content-type': types[extname(p)] ?? 'application/octet-stream' })
    res.end(readFileSync(p))
  }).listen(4321, () => console.log(`Serving on http://localhost:4321${base}`))
}
