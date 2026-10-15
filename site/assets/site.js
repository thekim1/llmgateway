// Theme switch, mobile menu, tabs, copy buttons and the docs page filter. The site works without it, except the switches.
(() => {
  const root = document.documentElement
  const store = (k, v) => { try { v === undefined ? (v = localStorage.getItem(k)) : localStorage.setItem(k, v) } catch { v = null } return v }

  // Theme
  const themeButtons = document.querySelectorAll('[data-theme-set]')
  const showTheme = () => themeButtons.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.themeSet === root.dataset.theme)))
  themeButtons.forEach(b => b.addEventListener('click', () => { root.dataset.theme = b.dataset.themeSet; store('ume-site-theme', b.dataset.themeSet); showTheme() }))
  showTheme()

  // Mobile menu
  const menu = document.querySelector('.menu-btn'), mnav = document.getElementById('mnav')
  menu?.addEventListener('click', () => {
    const open = menu.getAttribute('aria-expanded') !== 'true'
    menu.setAttribute('aria-expanded', String(open))
    mnav.hidden = !open
    menu.querySelector('.ms').textContent = open ? 'close' : 'menu'
  })

  // Tabs (WAI-ARIA tabs pattern, automatic activation)
  document.querySelectorAll('[data-tabs]').forEach(group => {
    const tabs = [...group.querySelectorAll('[role="tab"]')].filter(t => t.closest('[data-tabs]') === group)
    const select = (tab, focus) => {
      tabs.forEach(t => {
        const on = t === tab
        t.setAttribute('aria-selected', String(on))
        t.tabIndex = on ? 0 : -1
        document.getElementById(t.getAttribute('aria-controls')).hidden = !on
      })
      if (focus) tab.focus()
      if (tab.dataset.hash) history.replaceState(null, '', '#' + tab.dataset.hash)
    }
    tabs.forEach((t, i) => {
      t.addEventListener('click', () => select(t))
      t.addEventListener('keydown', e => {
        const n = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key]
        if (n) { e.preventDefault(); select(tabs[(i + n + tabs.length) % tabs.length], true) }
        if (e.key === 'Home') { e.preventDefault(); select(tabs[0], true) }
        if (e.key === 'End') { e.preventDefault(); select(tabs[tabs.length - 1], true) }
      })
    })
    const fromHash = tabs.find(t => t.dataset.hash && '#' + t.dataset.hash === location.hash)
    if (fromHash) select(fromHash)
  })

  // Copy
  const region = document.querySelector('.toast-region')
  let timer
  const copied = (button, text) => {
    navigator.clipboard?.writeText(text).catch(() => {})
    const icon = button.querySelector('.ms'), label = button.querySelector('span:last-child')
    icon.textContent = 'check'; label.textContent = 'Copied'
    region.innerHTML = '<div class="toast">Copied to clipboard</div>'
    clearTimeout(timer)
    timer = setTimeout(() => { icon.textContent = 'content_copy'; label.textContent = 'Copy'; region.innerHTML = '' }, 2200)
  }
  document.querySelectorAll('[data-copy]').forEach(b => b.addEventListener('click', () => copied(b, b.closest('.code').querySelector('pre').innerText)))
  document.querySelectorAll('[data-copy-active]').forEach(b => b.addEventListener('click', () => copied(b, b.closest('[data-tabs]').querySelector('[role="tabpanel"]:not([hidden])').innerText)))

  // Docs: filter the sidebar, jump with the select on small screens, highlight the current section.
  const filter = document.querySelector('[data-doc-filter]')
  filter?.addEventListener('input', () => {
    const q = filter.value.trim().toLowerCase()
    document.querySelectorAll('.side-g').forEach(g => {
      let any = false
      g.querySelectorAll('a').forEach(a => { const hit = !q || a.textContent.toLowerCase().includes(q); a.hidden = !hit; any ||= hit })
      g.hidden = !any
    })
  })
  document.querySelector('[data-nav]')?.addEventListener('change', e => { location.href = e.target.value })
  const tocLinks = [...document.querySelectorAll('.toc a')]
  if (tocLinks.length && 'IntersectionObserver' in window) {
    const visible = new Set()
    const io = new IntersectionObserver(entries => {
      entries.forEach(e => e.isIntersecting ? visible.add(e.target.id) : visible.delete(e.target.id))
      const first = tocLinks.find(a => visible.has(a.hash.slice(1)))
      if (first) tocLinks.forEach(a => a.classList.toggle('on', a === first))
    }, { rootMargin: '-64px 0px -60% 0px' })
    tocLinks.forEach(a => { const h = document.getElementById(decodeURIComponent(a.hash.slice(1))); if (h) io.observe(h) })
  }
})()
