import { expect, test } from '@playwright/test'
import { confirmDialog, login, mutate, openRow, ready, takeSecret, testKey } from '../helpers'

// Close before fixture artifact collection so failed secret flows cannot produce DOM snapshots.
test.afterEach(async ({ page }) => { await page.close() })

test('OIDC login, team and show-once key, rotation, usage, budget and audit', async ({ page }) => {
  await login(page)
  const suffix = crypto.randomUUID().slice(0, 8)
  const departmentName = `Browser department ${suffix}`
  const teamName = `Browser team ${suffix}`
  const keyName = `Browser key ${suffix}`

  await ready(page, '/organisation')
  await page.getByRole('button', { name: 'New department' }).click()
  const departmentDrawer = page.getByRole('dialog', { name: 'New department' })
  await departmentDrawer.getByLabel('Name', { exact: true }).fill(departmentName)
  await departmentDrawer.getByLabel('Cost center').fill(suffix)
  await departmentDrawer.getByRole('button', { name: 'Create department' }).click()
  await expect(departmentDrawer).toBeHidden()
  await expect(page.getByRole('heading', { level: 2, name: departmentName })).toBeVisible()

  await page.getByRole('button', { name: 'Add team' }).click()
  const teamDrawer = page.getByRole('dialog', { name: 'Add team' })
  await teamDrawer.getByLabel('Name', { exact: true }).fill(teamName)
  await teamDrawer.getByRole('button', { name: 'Create team' }).click()
  const created = page.getByRole('dialog', { name: 'Team created' })
  await expect(created).toContainText(teamName)
  await created.getByRole('button', { name: 'Done' }).click()
  await expect(created).toBeHidden()

  await ready(page, '/keys')
  await page.getByRole('button', { name: 'New key' }).click()
  const keyDrawer = page.getByRole('dialog', { name: 'New key' })
  await keyDrawer.getByLabel('Team', { exact: true }).selectOption({ label: `${departmentName} · ${teamName}` })
  await keyDrawer.getByLabel('Name', { exact: true }).fill(keyName)
  const createdPromise = page.waitForResponse(r => r.url().endsWith('/api/keys') && r.request().method() === 'POST')
  await keyDrawer.getByRole('button', { name: 'Create key' }).click()
  const createdKey = await (await createdPromise).json()
  const secret = await takeSecret(page)
  await expect(page.getByTestId('secret')).toHaveCount(0)

  const catalogue = await (await page.request.get('/api/catalog')).json()
  const completion = await page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
    headers: { Authorization: `Bearer ${secret}` },
    data: { model: 'ume/demo-fallback', messages: [{ role: 'user', content: 'Synthetic browser demonstration' }], max_tokens: 32 },
  })
  expect(completion.status()).toBe(200)
  expect(completion.headers()['x-ume-fallbacks']).toBe('1')
  const requestId = completion.headers()['x-request-id']
  await expect.poll(async () => (await page.request.get(`/api/usage/requests/${requestId}`)).status()).toBe(200)
  const budget = await mutate(page, '/api/budgets', { scope: 'VirtualKey', scopeId: createdKey.key.id, limitSek: 0, period: 'Monthly', alertThresholds: [80, 100], isActive: true })
  expect(budget.status()).toBe(201)

  await ready(page, '/keys')
  await openRow(page, keyName)
  const detail = page.getByRole('dialog', { name: keyName })
  await detail.getByRole('button', { name: 'Rotate', exact: true }).click()
  const rotateDialog = page.getByRole('dialog', { name: `Rotate ${keyName}` })
  await expect(rotateDialog.getByRole('radio', { name: /Revoke old key now/ })).toBeChecked()
  const rotationPromise = page.waitForResponse(r => r.url().endsWith(`/api/keys/${createdKey.key.id}/rotate`) && r.request().method() === 'POST')
  await rotateDialog.getByRole('button', { name: 'Rotate key' }).click()
  const rotated = await (await rotationPromise).json()
  const rotatedSecret = await takeSecret(page)
  expect(rotatedSecret).toBe(rotated.secret)

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

  await ready(page, '/usage')
  await page.getByLabel('Request ID').fill(requestId)
  await page.getByRole('button', { name: 'Look up' }).click()
  await expect(page.getByRole('dialog', { name: keyName })).toContainText(requestId)
  await page.keyboard.press('Escape')
  await ready(page, '/audit')
  await expect(page.locator('main')).toContainText('VirtualKey')
})

test('viewer and department administrator permissions come from real OIDC claims', async ({ page, browser }) => {
  await login(page, 'viewer')
  await page.goto('/providers')
  await expect(page).toHaveURL(/\/forbidden/)
  await expect(page.getByRole('heading', { level: 1, name: 'No access' })).toBeVisible()
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
  await ready(page, '/getting-started')
  await page.getByRole('button', { name: 'Copy' }).first().click()
  expect((await page.evaluate(() => navigator.clipboard.readText())).length).toBeGreaterThan(0)
  const renewed = await mutate(page, '/bff/session/extend')
  expect(renewed.status()).toBe(200)
  await ready(page, '/operations')
  const health = await (await page.request.get('/api/ops/health')).json()
  expect(health.components.find((component: { name: string }) => component.name === 'gateway').status).toBe('Healthy')
  expect(health.versions.gateway).toMatch(/^\d+\.\d+\.\d+/)
  expect(health.usageWriter.capacity).toBe(10000)
  expect(health.usageWriter.queueDepth).toBeGreaterThanOrEqual(0)
  expect(health.usageWriter.consecutiveFailures).toBe(0)
  await expect(page.getByRole('heading', { name: 'Usage writer' })).toBeVisible()
  await page.getByRole('button', { name: 'Sign out' }).click()
  await page.waitForURL(/\/protocol\/openid-connect\/logout/)
  await page.locator('#kc-logout').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/')
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible()
  expect((await (await page.request.get('/bff/user')).json()).isAuthenticated).toBe(false)
})

test('revocation asks for confirmation and applies immediately', async ({ page }) => {
  await login(page)
  const key = await testKey(page)
  const catalogue = await (await page.request.get('/api/catalog')).json()
  await ready(page, '/keys')
  const record = await (await page.request.get(`/api/keys/${key.id}`)).json()
  await page.getByRole('searchbox', { name: 'Search keys' }).fill(record.name)
  await openRow(page, record.name)
  const detail = page.getByRole('dialog', { name: record.name })
  await detail.getByRole('button', { name: 'Revoke', exact: true }).click()
  const confirm = page.getByRole('dialog', { name: `Revoke ${record.name}?` })
  await expect(confirm).toContainText('key_revoked')
  await confirm.getByRole('button', { name: 'Cancel' }).click()
  await expect(confirm).toBeHidden()
  expect((await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${key.secret}` } })).status()).toBe(200)
  await detail.getByRole('button', { name: 'Revoke', exact: true }).click()
  await confirmDialog(page, `Revoke ${record.name}?`, 'Revoke key')
  await expect(detail.getByRole('button', { name: 'Revoke', exact: true })).toHaveAttribute('aria-disabled', 'true')
  await expect.poll(async () => (await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, {
    headers: { Authorization: `Bearer ${key.secret}` },
  })).status()).toBe(401)
})

test('24 hour rotation keeps both keys usable until the predecessor is revoked', async ({ page }) => {
  await login(page)
  const key = await testKey(page)
  const catalogue = await (await page.request.get('/api/catalog')).json()
  const record = await (await page.request.get(`/api/keys/${key.id}`)).json()
  await ready(page, '/keys')
  await page.getByRole('searchbox', { name: 'Search keys' }).fill(record.name)
  await openRow(page, record.name)
  await page.getByRole('dialog', { name: record.name }).getByRole('button', { name: 'Rotate', exact: true }).click()
  const rotateDialog = page.getByRole('dialog', { name: `Rotate ${record.name}` })
  await rotateDialog.getByRole('radio', { name: /Keep old key for 24 hours/ }).check()
  const rotationPromise = page.waitForResponse(r => r.url().endsWith(`/api/keys/${key.id}/rotate`) && r.request().method() === 'POST')
  await rotateDialog.getByRole('button', { name: 'Rotate key' }).click()
  const rotated = await (await rotationPromise).json()
  expect(rotated.previousKey.status).toBe('InGracePeriod')
  const graceMilliseconds = Date.parse(rotated.previousKey.graceUntil) - Date.now()
  expect(graceMilliseconds).toBeGreaterThan(23.99 * 60 * 60 * 1000)
  expect(graceMilliseconds).toBeLessThanOrEqual(24 * 60 * 60 * 1000)
  await takeSecret(page)
  for (const secret of [key.secret, rotated.secret]) {
    const response = await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${secret}` } })
    expect(response.status()).toBe(200)
  }
  const revoked = await mutate(page, `/api/keys/${key.id}/revoke`)
  expect(revoked.ok()).toBe(true)
  await expect.poll(async () => (await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, {
    headers: { Authorization: `Bearer ${key.secret}` },
  })).status()).toBe(401)
  const replacement = await page.request.get(`${catalogue.gatewayBaseUrl}/v1/models`, { headers: { Authorization: `Bearer ${rotated.secret}` } })
  expect(replacement.status()).toBe(200)
})
