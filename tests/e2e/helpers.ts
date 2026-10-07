import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { expect, type BrowserContext, type Page } from '@playwright/test'

type Role = 'gateway-admin' | 'department-admin' | 'viewer'
const sessions = new Map<Role, Awaited<ReturnType<BrowserContext['cookies']>>>()
let nextAdminRequest = 0

async function paceAdminRequest(): Promise<void> {
  // Keep the single browser worker below the real 120 requests/minute limit.
  const slot = Math.max(Date.now(), nextAdminRequest)
  nextAdminRequest = slot + 600
  await new Promise(resolve => setTimeout(resolve, Math.max(0, slot - Date.now())))
}

function developmentPassword(): string {
  if (process.env.E2E_PASSWORD) return process.env.E2E_PASSWORD
  const directory = process.env.APPDATA
    ? join(process.env.APPDATA, 'Microsoft', 'UserSecrets')
    : join(homedir(), '.microsoft', 'usersecrets')
  const secrets = JSON.parse(readFileSync(join(directory, 'ume-llm-gateway-apphost-dev', 'secrets.json'), 'utf8').replace(/^\uFEFF/, ''))
  const password: unknown = secrets['Parameters:dev-user-password']
  if (typeof password !== 'string' || !password) throw new Error('Start the AppHost to generate development credentials, or set E2E_PASSWORD.')
  return password
}

export async function login(page: Page, role: Role = 'gateway-admin'): Promise<void> {
  await page.route('**/api/**', async route => {
    await paceAdminRequest()
    await route.continue()
  })
  const cached = sessions.get(role)
  if (cached) {
    await page.context().addCookies(cached)
    await page.goto('/')
  } else {
    await page.goto('/')
    await page.getByRole('link', { name: 'Logga in', exact: true }).click()
    await page.locator('#username').fill(role)
    // evaluate keeps the password out of locator call logs if a later assertion fails.
    await page.locator('#password').evaluate((element, password) => {
      const input = element as HTMLInputElement
      input.value = password
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }, developmentPassword())
    await page.locator('#kc-login').click()
    await page.waitForURL(url => url.origin === new URL(process.env.ADMIN_UI_URL ?? 'https://localhost:5173').origin)
    await expect(page.getByRole('navigation', { name: 'Huvudmeny' })).toBeVisible()
    sessions.set(role, await page.context().cookies())
  }
  const user = await page.request.get('/bff/user')
  expect(user.ok()).toBe(true)
  expect((await user.json()).roles).toContain(role)
}

export async function confirmReview(page: Page): Promise<void> {
  const dialog = page.getByRole('alertdialog')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('checkbox').check()
  await dialog.getByTestId('confirm-action').click()
  await expect(dialog).toBeHidden()
}

export async function mutate(page: Page, path: string, body?: unknown) {
  const csrf = (await page.context().cookies()).find(cookie => cookie.name === 'XSRF-TOKEN')?.value
  if (!csrf) throw new Error('Missing browser CSRF cookie')
  if (path.startsWith('/api/')) await paceAdminRequest()
  return page.request.post(path, { data: body, headers: { 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': decodeURIComponent(csrf) } })
}

export async function testKey(page: Page): Promise<{ id: string; secret: string }> {
  await paceAdminRequest()
  const teamsResponse = await page.request.get('/api/teams')
  expect(teamsResponse.status()).toBe(200)
  const teams = await teamsResponse.json()
  const response = await mutate(page, '/api/keys', {
    teamId: teams[0].id, name: `Browser ${crypto.randomUUID()}`,
    allowedModels: [], allowedResidencies: [], piiPolicy: 'RerouteToOnPrem',
  })
  expect(response.status()).toBe(201)
  const created = await response.json()
  return { id: created.key.id, secret: created.secret }
}

export const themes = ['umea-light', 'umea-dark', 'hc-light', 'hc-dark'] as const
export const pages = ['/', '/organisation', '/organisation/nytt-team', '/nycklar', '/nycklar/ny',
  '/leverantorer', '/modeller', '/rutter', '/budgetar', '/anvandning', '/granskning',
  '/kom-igang', '/modellkatalog', '/drift', '/installningar', '/ingen-behorighet', '/saknas']

export async function ready(page: Page, path: string): Promise<void> {
  await page.goto(path)
  await expect(page.locator('#page-title')).toBeVisible()
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)
  await expect(page.locator('.async-state > .notice--danger, .async-state > .notice--warning')).toHaveCount(0)
  await page.evaluate(() => document.fonts.ready)
}
