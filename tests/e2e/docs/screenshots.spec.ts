import { expect, test, type Page } from '@playwright/test'
import { login, mutate, ready, remove } from '../helpers'

// Builds the two routing examples from the README through the real UI and photographs each step into docs/images.
// Only demo data and synthetic values appear on screen; no secrets are shown.
const images = '../../docs/images'
const FALLBACK = 'Advanced model with fallback'
const PII = 'Personal data stays on-prem'

async function setLightTheme(page: Page): Promise<void> {
  await page.evaluate(() => { try { localStorage.setItem('ume-theme', 'light') } catch { /* ignore */ } })
  await page.reload()
}

/** Waits for the toast to go away so it does not cover the screenshot, and lets the page settle. */
async function settle(page: Page): Promise<void> {
  await expect(page.getByText('Rule saved')).toBeHidden({ timeout: 20_000 })
  await page.waitForTimeout(300)
}

async function removeRule(page: Page, name: string): Promise<void> {
  const rules: { id: string; name: string }[] = await (await page.request.get('/api/routing-rules')).json()
  for (const rule of rules.filter(r => r.name === name)) await remove(page, `/api/routing-rules/${rule.id}`)
}

test('routing example screenshots', async ({ page }) => {
  await login(page)
  await setLightTheme(page)
  await removeRule(page, FALLBACK)
  await removeRule(page, PII)

  // 1. Fallback to another model when the main model does not answer.
  await ready(page, '/routing-rules')
  await page.getByRole('button', { name: 'New rule' }).click()
  let drawer = page.getByRole('dialog', { name: 'New rule' })
  await drawer.getByLabel('Name', { exact: true }).fill(FALLBACK)
  await drawer.getByLabel('Description').fill('If ume/chat-advanced does not answer, use ume/chat-standard.')
  await drawer.locator('#rule-condition').fill('model == "ume/chat-advanced"')
  await drawer.locator('#rule-target-0').fill('ume/chat-advanced')
  await drawer.getByRole('button', { name: 'Add fallback' }).click()
  await drawer.locator('#rule-fallback-0').fill('ume/chat-standard')
  await expect(drawer.getByRole('status').filter({ hasText: 'Valid' })).toBeVisible()
  await drawer.evaluate(el => { const s = el.querySelector('[class*="overflow-y"]') ?? el; s.scrollTop = 0 })
  await page.screenshot({ path: `${images}/routing-fallback-form.png` })
  const created = page.waitForResponse(r => r.url().endsWith('/api/routing-rules') && r.request().method() === 'POST')
  await drawer.getByRole('button', { name: 'Create rule' }).click()
  expect((await created).status()).toBe(201)
  await expect(drawer).toBeHidden()
  await expect(page.getByRole('heading', { level: 2, name: FALLBACK, exact: true })).toBeVisible()
  await settle(page)
  await page.screenshot({ path: `${images}/routing-fallback-rule.png`, clip: { x: 0, y: 0, width: 1280, height: 860 } })

  // 2. Personal data stays on-prem.
  await page.getByRole('button', { name: 'New rule' }).click()
  drawer = page.getByRole('dialog', { name: 'New rule' })
  await drawer.getByLabel('Name', { exact: true }).fill(PII)
  await drawer.getByLabel('Description').fill('Chats that contain personal data only go to our own servers. No fallbacks, so they never leave.')
  await drawer.locator('#rule-condition').fill('pii_detected && endpoint == "chat_completions"')
  await drawer.locator('#rule-target-0').fill('ume/chat-onprem')
  await expect(drawer.getByRole('status').filter({ hasText: 'Valid' })).toBeVisible()
  await drawer.evaluate(el => { const s = el.querySelector('[class*="overflow-y"]') ?? el; s.scrollTop = 0 })
  await page.screenshot({ path: `${images}/routing-pii-form.png` })
  const created2 = page.waitForResponse(r => r.url().endsWith('/api/routing-rules') && r.request().method() === 'POST')
  await drawer.getByRole('button', { name: 'Create rule' }).click()
  expect((await created2).status()).toBe(201)
  await expect(drawer).toBeHidden()
  await expect(page.getByRole('heading', { level: 2, name: PII, exact: true })).toBeVisible()

  // The first matching rule wins, so the personal-data rule must be checked before the others.
  const earlier = page.getByRole('button', { name: 'Check earlier' })
  while (await earlier.isEnabled() && !(await earlier.getAttribute('aria-disabled'))) {
    await earlier.click()
    await page.waitForTimeout(700)
  }
  await settle(page)
  await page.screenshot({ path: `${images}/routing-pii-rule.png`, clip: { x: 0, y: 0, width: 1280, height: 860 } })

  // 3. Test a request that contains personal data.
  await page.getByRole('button', { name: 'Test a request' }).click()
  const test = page.getByRole('dialog', { name: 'Test a request' })
  await test.getByLabel('Model the client asks for').fill('ume/chat-standard')
  await test.getByText('Usage values').click()
  await test.getByLabel('Personal data').selectOption({ label: 'Personal data found' })
  await page.screenshot({ path: `${images}/routing-pii-test-input.png`, clip: { x: 0, y: 0, width: 1280, height: 1180 } })
  await test.getByRole('button', { name: 'Run test' }).click()
  await expect(test.getByRole('heading', { name: `${PII} applies` })).toBeVisible()
  await test.getByRole('heading', { name: `${PII} applies` }).scrollIntoViewIfNeeded()
  await page.waitForTimeout(300)
  await page.screenshot({ path: `${images}/routing-pii-test-result.png`, clip: { x: 0, y: 0, width: 1280, height: 1180 } })
  await test.getByRole('button', { name: 'Close', exact: true }).click()

  // 4. Prove it on the real gateway: a key that allows personal data, a chat with a (synthetic) personnummer.
  const teams = await (await page.request.get('/api/teams')).json()
  const keyResponse = await mutate(page, '/api/keys', { teamId: teams[0].id, name: `Docs ${crypto.randomUUID()}`, allowedModels: [], allowedResidencies: [], piiPolicy: 'Allow' })
  expect(keyResponse.status()).toBe(201)
  const key = await keyResponse.json()
  try {
    const catalogue = await (await page.request.get('/api/catalog')).json()
    const send = async (content: string) => page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
      headers: { Authorization: `Bearer ${key.secret}` },
      data: { model: 'ume/chat-standard', messages: [{ role: 'user', content }], max_tokens: 16 },
    })
    await expect.poll(async () => (await send('Personnummer 19121212-1212')).headers()['x-ume-rule'] ?? '').not.toBe('')
    const withPii = await send('Personnummer 19121212-1212')
    const without = await send('Hej, vad är klockan?')
    console.log('WITH PII    ', withPii.status(), withPii.headers()['x-ume-provider'], withPii.headers()['x-ume-residency'], withPii.headers()['x-ume-rule'])
    console.log('WITHOUT PII ', without.status(), without.headers()['x-ume-provider'], without.headers()['x-ume-residency'], without.headers()['x-ume-rule'])
    expect(withPii.headers()['x-ume-residency']).toBe('OnPrem')
  } finally {
    await mutate(page, `/api/keys/${key.key.id}/revoke`)
  }

  // 5. Getting started.
  await ready(page, '/getting-started')
  const card = page.getByRole('heading', { name: 'Routing examples' }).locator('xpath=ancestor::*[contains(@class,"material")][1]')
  await card.screenshot({ path: `${images}/getting-started-routing.png` })
})
