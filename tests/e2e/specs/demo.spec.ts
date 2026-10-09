import { expect, test } from '@playwright/test'
import { login, mutate, ready } from '../helpers'

test.afterEach(async ({ page }) => { await page.close() })

test('final synthetic demo: fallback, PII on-prem, budget block, live writer and theme switch', async ({ page }) => {
  await login(page)
  const suffix = crypto.randomUUID().slice(0, 8)
  const departmentResponse = await mutate(page, '/api/departments', { name: `Demo ${suffix}`, costCenterCode: `demo-${suffix}` })
  expect(departmentResponse.status()).toBe(201)
  const department = await departmentResponse.json()
  const teamResponse = await mutate(page, '/api/teams', { departmentId: department.id, name: `Demo ${suffix}` })
  expect(teamResponse.status()).toBe(201)
  const team = await teamResponse.json()
  const keyResponse = await mutate(page, '/api/keys', {
    teamId: team.id, name: `Demo ${suffix}`, allowedModels: [], allowedResidencies: [], piiPolicy: 'RerouteToOnPrem',
  })
  expect(keyResponse.status()).toBe(201)
  const key = await keyResponse.json()
  try {
    const catalogue = await (await page.request.get('/api/catalog')).json()
    const call = (model: string, content: string) => page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
      headers: { Authorization: `Bearer ${key.secret}` },
      data: { model, messages: [{ role: 'user', content }], max_tokens: 32 },
    }).catch(() => { throw new Error('Synthetic gateway request failed; request details suppressed') })
    const fallback = await call('ume/demo-fallback', 'Synthetic demonstration')
    expect(fallback.status()).toBe(200)
    expect(fallback.headers()['x-ume-fallbacks']).toBe('1')
    expect(fallback.headers()['x-ume-provider']).toBe('fake-eu')
    const rerouted = await call('ume/chat-standard', 'Synthetic fixture 19121212-1212')
    expect(rerouted.status()).toBe(200)
    expect(rerouted.headers()['x-ume-residency']).toBe('OnPrem')
    expect(rerouted.headers()['x-ume-provider']).toBe('fake-onprem')
    const requestId = rerouted.headers()['x-request-id']
    await expect.poll(async () => (await page.request.get(`/api/usage/requests/${requestId}`)).status()).toBe(200)
    const budget = await mutate(page, '/api/budgets', {
      scope: 'VirtualKey', scopeId: key.key.id, limitSek: 0, period: 'Monthly', alertThresholds: [80, 100], isActive: true,
    })
    expect(budget.status()).toBe(201)
    expect((await call('ume/chat-standard', 'Synthetic budget check')).status()).toBe(402)
    await ready(page, '/operations')
    await expect.poll(async () => {
      const health = await (await page.request.get('/api/ops/health')).json()
      return health.usageWriter?.queueDepth === 0 && health.usageWriter?.inFlightRecords === 0 && health.usageWriter?.lastWriteAt !== null
    }).toBe(true)
    await ready(page, '/')
    const themeGroup = page.getByRole('radiogroup', { name: 'Theme' })
    for (const [label, theme] of [['Dark', 'dark'], ['Lumen', 'lumen'], ['Light', 'light']]) {
      await themeGroup.getByRole('radio', { name: label }).click()
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
    }
  } finally {
    await mutate(page, `/api/keys/${key.key.id}/revoke`)
    key.secret = ''
  }
})
