import { expect, test, type Page } from '@playwright/test'
import { confirmDialog, login, mutate, ready, remove, testTeam } from '../helpers'

// Automated stand-ins for the screen reader checklist in HANDOVER.md. They cover what a script can
// prove (focus, names, heading levels, language). They are NOT a screen reader pass: what is announced still has to be
// listened to with NVDA or VoiceOver.
test.afterEach(async ({ page }) => { await page.close() })

const suffix = () => crypto.randomUUID().slice(0, 8)

async function createRule(page: Page, body: Record<string, unknown>): Promise<string> {
  const response = await mutate(page, '/api/routing-rules', { targets: [{ model: 'ume/chat-onprem', weight: 1 }], scope: 'Global', ...body })
  expect(response.status(), await response.text()).toBe(201)
  return (await response.json()).id
}

test('focus moves to the detail heading, back to the trigger, to the first error and to the test result', async ({ page }) => {
  await login(page)
  const s = suffix()
  const id = await createRule(page, { name: `Browser focus rule ${s}`, condition: `headers["x-e2e"] == "${s}"` })
  try {
    await ready(page, '/routing-rules')
    await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name: `Browser focus rule ${s}` }).click()
    await expect(page.locator('#rule-title')).toBeFocused()

    // Drawer: Escape returns focus to the button that opened it.
    const trigger = page.getByRole('button', { name: 'New rule' })
    await trigger.click()
    const drawer = page.getByRole('dialog', { name: 'New rule' })
    await expect(drawer).toBeVisible()
    // A failed submit focuses the first invalid field.
    await drawer.getByRole('button', { name: 'Create rule' }).click()
    await expect(drawer.locator('#rule-name')).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(drawer).toBeHidden()
    await expect(trigger).toBeFocused()

    // The test dialog moves focus to the result heading, and Close returns it to the trigger.
    const testTrigger = page.getByRole('button', { name: 'Test a request' })
    await testTrigger.click()
    const dialog = page.getByRole('dialog', { name: 'Test a request' })
    await dialog.getByLabel('Model the client asks for').fill('ume/chat-standard')
    await dialog.getByRole('button', { name: 'Run test', exact: true }).click()
    await expect(dialog.getByRole('heading', { name: /applies|No rule matches/ })).toBeFocused()
    await dialog.getByRole('button', { name: 'Close', exact: true }).click()
    await expect(testTrigger).toBeFocused()

    // After deleting, focus is somewhere useful (not lost on the body).
    await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name: `Browser focus rule ${s}` }).click()
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirmDialog(page, 'Delete rule?', 'Delete rule')
    await expect.poll(() => page.evaluate(() => document.activeElement?.tagName ?? 'NONE')).not.toBe('BODY')
  } finally { await remove(page, `/api/routing-rules/${id}`) }
})

test('headings do not skip levels and controls have names', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button').first().click()
  const levels = await page.locator('main h1, main h2, main h3, main h4').evaluateAll(headings => headings.map(h => Number(h.tagName.slice(1))))
  expect(levels[0]).toBe(1)
  levels.forEach((level, i) => { if (i > 0) expect(level - levels[i - 1], `heading outline: ${levels.join(',')}`).toBeLessThanOrEqual(1) })
  const unnamed = await page.locator('main button, main a[href], main input, main select, main textarea').evaluateAll(elements =>
    elements.filter(e => {
      const label = e.getAttribute('aria-label') ?? e.getAttribute('aria-labelledby') ?? (e as HTMLInputElement).labels?.[0]?.textContent ?? e.textContent ?? ''
      return label.trim() === ''
    }).map(e => e.outerHTML.slice(0, 80)))
  expect(unnamed).toEqual([])
})

test('move buttons stay focusable and say they are unavailable at the ends', async ({ page }) => {
  await login(page)
  const s = suffix()
  const teamId = (await testTeam(page)).teamId
  const ids = [
    await createRule(page, { name: `Browser first ${s}`, scope: 'Team', scopeId: teamId, condition: 'model == "none"' }),
    await createRule(page, { name: `Browser second ${s}`, scope: 'Team', scopeId: teamId, condition: 'model == "none"' }),
  ]
  try {
    await ready(page, '/routing-rules')
    const list = page.getByRole('navigation', { name: 'Routing rules' })
    const expectations = [[`Browser first ${s}`, 'Check earlier', 'Check later'], [`Browser second ${s}`, 'Check later', 'Check earlier']]
    for (const [rule, unavailable, available] of expectations) {
      await list.getByRole('button', { name: rule }).click()
      const end = page.getByRole('button', { name: unavailable })
      await expect(end).toHaveAttribute('aria-disabled', 'true')
      await end.focus()
      await expect(end).toBeFocused()
      await expect(page.getByRole('button', { name: available })).not.toHaveAttribute('aria-disabled', 'true')
    }
    // The owner's name is marked as Swedish text.
    await expect(page.locator('section[aria-labelledby="rule-title"] [lang="sv"]').first()).toBeVisible()
  } finally { for (const id of ids) await remove(page, `/api/routing-rules/${id}`) }
})

test('model name inputs offer their suggestions as a datalist', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  await page.getByRole('button', { name: 'New rule' }).click()
  const drawer = page.getByRole('dialog', { name: 'New rule' })
  const input = drawer.locator('#rule-target-0')
  await expect(input).toHaveAttribute('list', 'rule-model-names')
  expect(await page.locator('#rule-model-names option').count()).toBeGreaterThan(0)
  await expect(input).toHaveAccessibleName('Model or route')
})
