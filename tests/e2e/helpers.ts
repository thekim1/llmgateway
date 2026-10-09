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
    await page.getByRole('link', { name: 'Sign in', exact: true }).click()
    await page.locator('#username').fill(role)
    // evaluate keeps the password out of locator call logs if a later assertion fails.
    await page.locator('#password').evaluate((element, password) => {
      const input = element as HTMLInputElement
      input.value = password
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }, developmentPassword())
    await page.locator('#kc-login').click()
    await page.waitForURL(url => url.origin === new URL(process.env.ADMIN_UI_URL ?? 'https://localhost:5173').origin)
    await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()
    sessions.set(role, await page.context().cookies())
  }
  const user = await page.request.get('/bff/user')
  expect(user.ok()).toBe(true)
  expect((await user.json()).roles).toContain(role)
}

/** Clicks a button in the open dialog or drawer with the given accessible name and waits for it to close. */
export async function confirmDialog(page: Page, dialogName: string | RegExp, buttonName: string | RegExp): Promise<void> {
  const dialog = page.getByRole('dialog', { name: dialogName })
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: buttonName, exact: typeof buttonName === 'string' }).click()
  await expect(dialog).toBeHidden()
}

/** Reads the show-once secret, ticks the acknowledgement and closes the dialog. */
export async function takeSecret(page: Page): Promise<string> {
  const dialog = page.getByRole('dialog', { name: 'Copy your secret now' })
  await expect(dialog).toBeVisible()
  const secret = (await dialog.getByTestId('secret').textContent())!.trim()
  await page.keyboard.press('Escape')
  await expect(dialog, 'the secret dialog must not close before it is acknowledged').toBeVisible()
  await dialog.getByRole('checkbox', { name: /stored the secret/ }).check()
  await dialog.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(dialog).toBeHidden()
  return secret
}

async function send(page: Page, method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown) {
  const csrf = (await page.context().cookies()).find(cookie => cookie.name === 'XSRF-TOKEN')?.value
  if (!csrf) throw new Error('Missing browser CSRF cookie')
  if (path.startsWith('/api/')) await paceAdminRequest()
  return page.request.fetch(path, { method, data: body, headers: { 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': decodeURIComponent(csrf) } })
}

export const mutate = (page: Page, path: string, body?: unknown) => send(page, 'POST', path, body)
export const remove = (page: Page, path: string) => send(page, 'DELETE', path)

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

/** A fresh department and team through the API, so a test can change them without touching the demo data. */
export async function testTeam(page: Page): Promise<{ departmentId: string; teamId: string; departmentName: string; teamName: string }> {
  const suffix = crypto.randomUUID().slice(0, 8)
  const departmentName = `Browser department ${suffix}`
  const teamName = `Browser team ${suffix}`
  const department = await mutate(page, '/api/departments', { name: departmentName, costCenterCode: `e2e-${suffix}` })
  expect(department.status()).toBe(201)
  const departmentId = (await department.json()).id
  const team = await mutate(page, '/api/teams', { departmentId, name: teamName })
  expect(team.status()).toBe(201)
  return { departmentId, teamId: (await team.json()).id, departmentName, teamName }
}

export async function gatewayCall(page: Page, secret: string, headers: Record<string, string>) {
  const catalogue = await (await page.request.get('/api/catalog')).json()
  return page.request.post(`${catalogue.gatewayBaseUrl}/v1/chat/completions`, {
    headers: { Authorization: `Bearer ${secret}`, ...headers },
    data: { model: 'ume/chat-standard', messages: [{ role: 'user', content: 'Synthetic routing rule check' }], max_tokens: 16 },
  })
}

/** The x-ume-rule header of a request that carries the marker header, or null when no rule applied. */
export async function appliedRule(page: Page, secret: string, marker: string): Promise<string | null> {
  const response = await gatewayCall(page, secret, { 'x-e2e': marker })
  expect(response.status()).toBe(200)
  return response.headers()['x-ume-rule'] ?? null
}

export const themes = ['light', 'dark', 'lumen'] as const
export const themeStorageKey = 'ume-theme'

/** Every page in src/admin-ui/src/router/index.ts, plus the 404 page. Keep in sync with the router. */
export const pages = ['/', '/usage', '/operations', '/keys', '/organisation', '/catalog', '/getting-started',
  '/routes', '/routing-rules', '/providers', '/budgets', '/audit', '/settings', '/forbidden', '/no-such-page']

export async function ready(page: Page, path: string): Promise<void> {
  await page.goto(path)
  await expect(page.locator('#page-title')).toBeVisible()
  await expect(page.locator('main .animate-spin')).toHaveCount(0)
  // A failed load shows an alert with a "Try again" button; other alerts (a rule without an owner) are content.
  await expect(page.locator('main [role="alert"]').filter({ has: page.getByRole('button', { name: 'Try again' }) })).toHaveCount(0)
  await page.evaluate(() => document.fonts.ready)
}

/** Clicks the row button of a data table row by its visible text. */
export async function openRow(page: Page, text: string): Promise<void> {
  await page.getByRole('row').filter({ hasText: text }).getByRole('button').first().click()
}

/**
 * Targets that fail WCAG 2.2 SC 2.5.8 (Target Size, Minimum, AA). A target passes when it is at least 24 x 24 px, or when a
 * 24 px circle centred on it does not touch any other target (the spacing exception). Checkboxes and radios are skipped:
 * their label is the target.
 */
export async function undersizedControls(page: Page, minimum = 24) {
  return page.evaluate(min => {
    const selector = 'button, a[href], input:not([type="hidden"]), select, textarea, summary, [role="button"], [tabindex]:not([tabindex="-1"])'
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect()
      const style = getComputedStyle(element)
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && !element.closest('[aria-hidden="true"], [inert]')
    }
    const all = [...document.querySelectorAll(selector)].filter(visible)
    const rects = all.map(element => ({ element, rect: element.getBoundingClientRect() }))
    const failures: { id: string; text: string; width: number; height: number }[] = []
    for (const { element, rect } of rects) {
      if (element.matches('input[type="checkbox"], input[type="radio"]') || rect.width >= min && rect.height >= min) continue
      const cx = rect.left + rect.width / 2
      const cy = rect.top + rect.height / 2
      const crowded = rects.some(other => {
        if (other.element === element || other.element.contains(element) || element.contains(other.element)) return false
        const dx = Math.max(other.rect.left - cx, 0, cx - other.rect.right)
        const dy = Math.max(other.rect.top - cy, 0, cy - other.rect.bottom)
        return Math.hypot(dx, dy) < min / 2
      })
      if (crowded) failures.push({ id: element.id, text: (element.textContent ?? '').trim().slice(0, 30), width: Math.round(rect.width), height: Math.round(rect.height) })
    }
    return failures
  }, minimum)
}
