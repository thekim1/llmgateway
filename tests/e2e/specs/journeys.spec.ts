import { expect, test } from '@playwright/test'
import { confirmReview, login, mutate, ready, testKey } from '../helpers'

// Close before fixture artifact collection so failed secret flows cannot produce DOM snapshots.
test.afterEach(async ({ page }) => { await page.close() })

test('OIDC login, team and show-once key, rotation, usage, budget and audit', async ({ page }) => {
  await login(page)
  const suffix = crypto.randomUUID().slice(0, 8)
  const departmentName = `Browser department ${suffix}`
  const teamName = `Browser team ${suffix}`
  const keyName = `Browser key ${suffix}`
  await ready(page, '/organisation')
  await page.locator('#departments-form-name').fill(departmentName)
  await page.locator('#departments-form-costCenterCode').fill(suffix)
  await page.locator('#departments-form-name').locator('xpath=ancestor::form').getByRole('button', { name: 'Granska ändringen' }).click()
  await confirmReview(page)
  await page.locator('#teams-form-departmentId').selectOption({ label: departmentName })
  await page.locator('#teams-form-name').fill(teamName)
  await page.locator('#teams-form-name').locator('xpath=ancestor::form').getByRole('button', { name: 'Granska ändringen' }).click()
  await confirmReview(page)
  await ready(page, '/nycklar/ny')
  await page.locator('#key-teamId').selectOption({ label: `${departmentName} · ${teamName}` })
  await page.locator('#key-name').fill(keyName)
  await page.getByRole('button', { name: 'Granska ändringen' }).click()
  const createdPromise = page.waitForResponse(r => r.url().endsWith('/api/keys') && r.request().method() === 'POST')
  await confirmReview(page)
  const created = await (await createdPromise).json()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  const secret = (await dialog.getByTestId('secret-value').textContent())!.trim()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeVisible()
  await dialog.locator('#secret-ack').check()
  await dialog.getByTestId('secret-close').click()
  await expect(page.getByTestId('secret-value')).toHaveCount(0)
  await expect(page).toHaveURL(new RegExp(`/nycklar/${created.key.id}$`))
  const catalogue = await (await page.request.get('/api/catalog')).json()
  const completion = await page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
    headers: { Authorization: `Bearer ${secret}` },
    data: { model: 'ume/demo-fallback', messages: [{ role: 'user', content: 'Synthetic browser demonstration' }], max_tokens: 32 },
  })
  expect(completion.status()).toBe(200)
  expect(completion.headers()['x-ume-fallbacks']).toBe('1')
  const requestId = completion.headers()['x-request-id']
  await expect.poll(async () => (await page.request.get(`/api/usage/requests/${requestId}`)).status()).toBe(200)
  const budget = await mutate(page, '/api/budgets', { scope: 'VirtualKey', scopeId: created.key.id, limitSek: 0, period: 'Monthly', alertThresholds: [80, 100], isActive: true })
  expect(budget.status()).toBe(201)
  await page.getByRole('button', { name: 'Rotera nyckeln', exact: true }).click()
  await expect(page.locator('#rotate-RevokeImmediately')).toBeChecked()
  const rotationPromise = page.waitForResponse(r => r.url().endsWith(`/api/keys/${created.key.id}/rotate`) && r.request().method() === 'POST')
  await page.getByRole('dialog').getByRole('button', { name: 'Rotera nyckeln', exact: true }).click()
  const rotated = await (await rotationPromise).json()
  await expect(page.getByTestId('secret-value')).toBeVisible()
  await page.locator('#secret-ack').check()
  await page.getByTestId('secret-close').click()
  const denied = await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${secret}` } })
  expect(denied.status()).toBe(401)
  const blocked = await page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
    headers: { Authorization: `Bearer ${rotated.secret}` },
    data: { model: 'ume/chat-standard', messages: [{ role: 'user', content: 'Synthetic budget check' }], max_tokens: 32 },
  })
  expect(blocked.status()).toBe(402)
  expect((await blocked.json()).error.code).toBe('budget_exceeded')
  const stored = await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } }))
  expect(JSON.stringify(stored).includes(secret)).toBe(false)
  await ready(page, '/anvandning')
  await page.locator('#request-id').fill(requestId)
  await page.getByRole('button', { name: 'Sök anrop via anrops-ID' }).click()
  await expect(page.locator('main')).toContainText(requestId)
  await ready(page, '/granskning')
  await expect(page.locator('main')).toContainText('VirtualKey')
})

test('viewer and department administrator permissions come from real OIDC claims', async ({ page, browser }) => {
  await login(page, 'viewer')
  await ready(page, '/leverantorer')
  await expect(page).toHaveURL(/ingen-behorighet/)
  expect((await page.request.get('/api/providers')).status()).toBe(403)
  const context = await browser.newContext({ baseURL: process.env.ADMIN_UI_URL ?? 'https://localhost:5173', ignoreHTTPSErrors: true })
  try {
    const scoped = await context.newPage()
    await login(scoped, 'department-admin')
    const departments = await (await scoped.request.get('/api/departments')).json()
    expect(departments.map((d: { costCenterCode: string }) => d.costCenterCode)).toEqual(['1000'])
    expect((await scoped.request.get('/api/providers')).status()).toBe(403)
  } finally { await context.close() }
})

test('portal copy, explicit session renewal, operations and OIDC logout', async ({ page }) => {
  await login(page)
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write'])
  await ready(page, '/kom-igang')
  await page.getByRole('button', { name: 'Kopiera' }).first().click()
  expect((await page.evaluate(() => navigator.clipboard.readText())).length).toBeGreaterThan(0)
  const renewed = await mutate(page, '/bff/session/extend')
  expect(renewed.status()).toBe(200)
  await ready(page, '/drift')
  const health = await (await page.request.get('/api/ops/health')).json()
  expect(health.components.find((component: { name: string }) => component.name === 'gateway').status).toBe('Healthy')
  expect(health.versions.gateway).toMatch(/^\d+\.\d+\.\d+/)
  expect(health.usageWriter.capacity).toBe(10000)
  expect(health.usageWriter.queueDepth).toBeGreaterThanOrEqual(0)
  expect(health.usageWriter.consecutiveFailures).toBe(0)
  await expect(page.getByRole('heading', { name: 'Lagring av användning' })).toBeVisible()
  await page.getByRole('button', { name: /Gateway Administrator/ }).click()
  await page.getByRole('menuitem', { name: 'Logga ut' }).click()
  await page.waitForURL(/\/protocol\/openid-connect\/logout/)
  await page.locator('#kc-logout').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/')
  await expect(page.getByRole('link', { name: 'Logga in', exact: true })).toBeVisible()
  expect((await (await page.request.get('/bff/user')).json()).isAuthenticated).toBe(false)
})

test('revocation requires acknowledgement and applies immediately', async ({ page }) => {
  await login(page)
  const key = await testKey(page)
  await ready(page, `/nycklar/${key.id}`)
  await page.getByRole('button', { name: 'Återkalla nyckeln', exact: true }).click()
  await confirmReview(page)
  await expect(page.getByRole('button', { name: 'Återkalla nyckeln', exact: true })).toBeDisabled()
})

test('24 hour rotation keeps both keys usable until the predecessor is revoked', async ({ page }) => {
  await login(page)
  const key = await testKey(page)
  const catalogue = await (await page.request.get('/api/catalog')).json()
  await ready(page, `/nycklar/${key.id}`)
  await page.getByRole('button', { name: 'Rotera nyckeln', exact: true }).click()
  await page.locator('#rotate-Grace24Hours').check()
  const rotationPromise = page.waitForResponse(r => r.url().endsWith(`/api/keys/${key.id}/rotate`) && r.request().method() === 'POST')
  await page.getByRole('dialog').getByRole('button', { name: 'Rotera nyckeln', exact: true }).click()
  const rotated = await (await rotationPromise).json()
  expect(rotated.previousKey.status).toBe('InGracePeriod')
  const graceMilliseconds = Date.parse(rotated.previousKey.graceUntil) - Date.now()
  expect(graceMilliseconds).toBeGreaterThan(23.99 * 60 * 60 * 1000)
  expect(graceMilliseconds).toBeLessThanOrEqual(24 * 60 * 60 * 1000)
  await page.locator('#secret-ack').check()
  await page.getByTestId('secret-close').click()
  for (const secret of [key.secret, rotated.secret]) {
    const response = await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${secret}` } })
    expect(response.status()).toBe(200)
  }
  await ready(page, `/nycklar/${key.id}`)
  await page.getByRole('button', { name: 'Återkalla nyckeln', exact: true }).click()
  await confirmReview(page)
  await expect.poll(async () => (await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, {
    headers: { Authorization: `Bearer ${key.secret}` },
  })).status()).toBe(401)
  const replacement = await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${rotated.secret}` } })
  expect(replacement.status()).toBe(200)
})
