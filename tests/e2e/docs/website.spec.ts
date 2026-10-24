import { expect, test, type Page } from '@playwright/test'
import { login, mutate, openRow, ready, themes } from '../helpers'

// Photographs the admin UI for the website (site/), in each theme, into site/public/screenshots. Only demo data appears;
// the one secret on screen belongs to a throw-away key that is masked before the picture and revoked afterwards.
const out = '../../site/public/screenshots'
type Theme = (typeof themes)[number]

async function setTheme(page: Page, theme: Theme): Promise<void> {
  await page.evaluate(t => { try { localStorage.setItem('ume-theme', t) } catch { /* ignore */ } }, theme)
}

/** Opens a page in a theme and waits until its data has loaded (the helpers pace API calls) and its charts have drawn. */
async function open(page: Page, path: string, theme: Theme): Promise<void> {
  await setTheme(page, theme)
  await ready(page, path)
  await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
  await page.waitForLoadState('networkidle')
  await expect(page.locator('main .animate-spin, main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
  await page.waitForTimeout(1000)
}

async function put(page: Page, path: string, body: unknown) {
  const csrf = (await page.context().cookies()).find(cookie => cookie.name === 'XSRF-TOKEN')!.value
  return page.request.fetch(path, { method: 'PUT', data: body, headers: { 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': decodeURIComponent(csrf) } })
}

type Budget = { id: string; scope: string; scopeId: string; limitUsd: number; period: string; alertThresholds: number[]; isActive: boolean; spentUsd: number }

test.describe.configure({ mode: 'serial' })

test('website screenshots', async ({ page }) => {
  test.setTimeout(1_200_000)
  await login(page)

  // Traffic from two applications in teams that have budgets, so Overview, Usage and Budgets have something to show.
  const teams: { id: string; name: string }[] = await (await page.request.get('/api/teams')).json()
  const apps = [['Ärendehandläggning', 'Kontaktcenter'], ['Webbchatt', 'Digitalisering & AI']] as const
  const keys: { id: string; secret: string }[] = []
  for (const [name, team] of apps) {
    const response = await mutate(page, '/api/keys', { teamId: teams.find(t => t.name === team)!.id, name, allowedModels: [], allowedResidencies: [], piiPolicy: 'Allow' })
    expect(response.status()).toBe(201)
    const body = await response.json()
    keys.push({ id: body.key.id, secret: body.secret })
  }
  const created = keys.map(k => k.id)
  try {
    // A budget for the web chat that will end up nearly used, so an alert is open.
    const budgetResponse = await mutate(page, '/api/budgets', { scope: 'VirtualKey', scopeId: keys[1].id, limitUsd: 1_000_000, period: 'Monthly', alertThresholds: [50, 80, 100], isActive: true })
    expect(budgetResponse.status()).toBe(201)
    const budget: Budget = await budgetResponse.json()
    const catalogue = await (await page.request.get('/api/catalog')).json()
    const models = ['ume/chat-standard', 'ume/chat-standard', 'ume/chat-advanced', 'ume/chat-onprem', 'ume/demo-fallback']
    const chat = (i: number) => page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
      headers: { Authorization: `Bearer ${keys[i % 2].secret}` },
      data: { model: models[i % models.length], messages: [{ role: 'user', content: i % 7 === 0 ? `Ring mig på 070-123 45 67 om ärende ${i}` : `Sammanfatta ärende ${i}` }], max_tokens: 256, stream: i % 3 === 0 },
    })
    for (let i = 0; i < 90; i += 6) await Promise.all([0, 1, 2, 3, 4, 5].map(j => chat(i + j)))
    // Usage is written in the background: wait for it, then set the limit so the web chat has used about 85 %.
    let spent = 0
    await expect.poll(async () => {
      const budgets: Budget[] = await (await page.request.get('/api/budgets?scope=VirtualKey')).json()
      spent = budgets.find(b => b.id === budget.id)?.spentUsd ?? 0
      return spent
    }, { timeout: 30_000 }).toBeGreaterThan(0)
    const limit = Math.max(0.01, Math.round(spent / 0.85 * 100) / 100)
    expect((await put(page, `/api/budgets/${budget.id}`, { ...budget, limitUsd: limit })).status()).toBe(200)
    // The fake LLM is cheap, so the smallest limit (0.01 US$) can be more than 85 % of what was spent: send web chat
    // requests until the budget is past its 80 % alert level, and stop well before it runs out.
    const topUp = () => page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
      headers: { Authorization: `Bearer ${keys[1].secret}` },
      data: { model: 'ume/chat-standard', messages: [{ role: 'user', content: 'Sammanfatta ärendet' }], max_tokens: 256 },
    })
    for (let round = 0; round < 60 && spent < limit * 0.82; round++) {
      const statuses = await Promise.all([0, 1, 2, 3].map(async () => (await topUp()).status()))
      expect(statuses, 'top-up requests succeed').toEqual([200, 200, 200, 200])
      await page.waitForTimeout(1200)
      const budgets: Budget[] = await (await page.request.get('/api/budgets?scope=VirtualKey')).json()
      spent = budgets.find(b => b.id === budget.id)?.spentUsd ?? spent
    }
    expect(spent).toBeGreaterThan(limit * 0.8)
    expect(spent).toBeLessThan(limit)

    for (const theme of themes) {
      // Overview, 1440 x 900 (the hero image).
      await page.setViewportSize({ width: 1440, height: 900 })
      await open(page, '/', theme)
      await page.screenshot({ path: `${out}/overview-desktop-${theme}.png` })

      // Keys, Budgets and Usage at 4:3.
      await page.setViewportSize({ width: 1280, height: 960 })
      await open(page, '/keys', theme)
      await openRow(page, 'Webbchatt')
      await expect(page.getByRole('dialog', { name: 'Webbchatt' })).toBeVisible()
      await page.waitForLoadState('networkidle')
      await page.waitForTimeout(800)
      await page.screenshot({ path: `${out}/keys-drawer-${theme}.png` })
      await page.keyboard.press('Escape')

      await open(page, '/budgets', theme)
      await page.screenshot({ path: `${out}/budgets-${theme}.png` })

      await open(page, '/usage', theme)
      await page.getByRole('button', { name: '24 hours' }).click()
      await page.waitForLoadState('networkidle')
      await page.waitForTimeout(1500)
      await page.screenshot({ path: `${out}/usage-${theme}.png` })

      // Getting started steps at 16:9.
      await page.setViewportSize({ width: 1280, height: 720 })
      await open(page, '/keys', theme)
      await page.getByRole('button', { name: 'New key' }).click()
      const drawer = page.getByRole('dialog', { name: 'New key' })
      await drawer.getByLabel('Team', { exact: true }).selectOption({ index: 1 })
      await drawer.getByLabel('Name', { exact: true }).fill(`Website ${theme}`)
      const keyCreated = page.waitForResponse(r => r.url().endsWith('/api/keys') && r.request().method() === 'POST')
      await drawer.getByRole('button', { name: 'Create key' }).click()
      created.push((await (await keyCreated).json()).key.id)
      const secretDialog = page.getByRole('dialog', { name: 'Copy your secret now' })
      await expect(secretDialog).toBeVisible()
      // Never publish a secret, even a throw-away one.
      await secretDialog.getByTestId('secret').evaluate(el => { el.textContent = 'ume-sk-' + '•'.repeat(40) })
      await page.waitForTimeout(400)
      await page.screenshot({ path: `${out}/secret-dialog-${theme}.png` })
      await secretDialog.getByRole('checkbox', { name: /stored the secret/ }).check()
      await secretDialog.getByRole('button', { name: 'Done', exact: true }).click()
      await expect(secretDialog).toBeHidden()

      await open(page, '/providers', theme)
      await page.getByRole('row').filter({ hasText: 'fake-eu' }).first().click()
      await page.getByRole('button', { name: 'Discover models' }).click()
      const discover = page.getByRole('dialog', { name: 'Discover models' })
      await expect(discover).toBeVisible()
      await expect(discover.getByText('Contacting provider')).toBeHidden({ timeout: 30_000 })
      await page.waitForTimeout(400)
      await page.screenshot({ path: `${out}/providers-discover-${theme}.png` })
      await page.keyboard.press('Escape')
    }
  } finally {
    for (const id of created) await mutate(page, `/api/keys/${id}/revoke`)
  }
})
