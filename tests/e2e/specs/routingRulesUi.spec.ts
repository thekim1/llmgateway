import { expect, test, type Page } from '@playwright/test'
import { appliedRule, login, mutate, ready, remove, testKey } from '../helpers'

test.afterEach(async ({ page }) => { await page.close() })

const suffix = () => crypto.randomUUID().slice(0, 8)
const marker = (s: string) => `headers["x-e2e"] == "${s}"`

async function createRule(page: Page, body: Record<string, unknown>): Promise<string> {
  const response = await mutate(page, '/api/routing-rules', { targets: [{ model: 'ume/chat-onprem', weight: 1 }], scope: 'Global', ...body })
  expect(response.status(), await response.text()).toBe(201)
  return (await response.json()).id
}

test('the scope filter counts match the rules', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  const rules: { scope: string }[] = await (await page.request.get('/api/routing-rules')).json()
  const filter = page.getByRole('group', { name: 'Filter rules by scope' })
  const count = async (label: string) => Number((await filter.getByRole('button', { name: new RegExp(`^${label}\\s*\\d+$`) }).textContent())!.replace(/\D/g, ''))
  expect(await count('All')).toBe(rules.length)
  expect(await count('Global')).toBe(rules.filter(r => r.scope === 'Global').length)
  await filter.getByRole('button', { name: /^Global/ }).click()
  await expect(filter.getByRole('button', { name: /^Global/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('navigation', { name: 'Routing rules' }).getByRole('heading', { level: 2 })).toHaveText(['Global rules'])
})

test('condition helpers, an unreachable checker, and weight shares', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  await page.getByRole('button', { name: 'New rule' }).click()
  const drawer = page.getByRole('dialog', { name: 'New rule' })
  const condition = drawer.locator('#rule-condition')

  await drawer.getByText('Variables and examples').click()
  await drawer.getByRole('button', { name: /^Insert model \(/ }).click()
  await expect(condition).toHaveValue(/model/)
  const example = drawer.getByText('Examples (replaces the condition):').locator('xpath=following-sibling::ul[1]//button').first()
  await example.click()
  await expect(condition).not.toHaveValue('')
  await expect(drawer.getByRole('status').filter({ hasText: 'Valid' })).toBeVisible()

  // The checker is down: the form says so and does not claim the condition is valid.
  await page.route('**/api/routing-rules/validate', route => route.abort())
  await condition.fill('model == "ume/chat-standard"')
  await expect(drawer.getByRole('status').filter({ hasText: /Couldn.t check the condition/ })).toBeVisible()
  await page.unroute('**/api/routing-rules/validate')

  // Two targets: shares follow the weights.
  await drawer.getByRole('button', { name: 'Add target' }).click()
  await drawer.getByLabel('Weight').nth(0).fill('3')
  await drawer.getByLabel('Weight').nth(1).fill('1')
  await expect(drawer.getByText('75% of the traffic')).toBeVisible()
  await expect(drawer.getByText('25% of the traffic')).toBeVisible()
})

test('the test dialog shows what could not be evaluated, chain steps and missing models', async ({ page }) => {
  await login(page)
  const s = suffix()
  const id = await createRule(page, { name: `Browser chain rule ${s}`, condition: marker(s), chain: true, targets: [{ model: `browser/missing-${s}`, weight: 1 }] })
  try {
    await ready(page, '/routing-rules')
    await page.getByRole('button', { name: 'Test a request' }).click()
    const dialog = page.getByRole('dialog', { name: 'Test a request' })
    const model = dialog.getByLabel('Model the client asks for')
    const run = dialog.getByRole('button', { name: 'Run test', exact: true })

    // No header sent: the header comparison cannot be evaluated.
    await model.fill('ume/chat-standard')
    await run.click()
    await expect(dialog.getByText('could not be evaluated (a value is missing)').first()).toBeVisible()

    // A chain: the old alias is renamed, then the premium rule routes it further.
    await model.fill('gpt-4')
    await dialog.getByText('Headers and request parameters').click()
    await dialog.getByRole('button', { name: 'Add header' }).click()
    await dialog.getByLabel('Header 1 name').fill('x-ume-tier')
    await dialog.getByLabel('Header 1 value').fill('premium')
    await run.click()
    await expect(dialog.getByRole('heading', { name: /Premium via header applies/ })).toBeVisible()
    await expect(dialog.getByText(/step 2 for/).first()).toBeVisible()

    // A chained rule may point at a name that does not exist; the result warns about it.
    await model.fill('ume/chat-standard')
    await dialog.getByLabel('Header 1 name').fill('x-e2e')
    await dialog.getByLabel('Header 1 value').fill(s)
    await run.click()
    await expect(dialog.getByText('Does not exist')).toBeVisible()
  } finally { await remove(page, `/api/routing-rules/${id}`) }
})

test('moving a rule earlier changes which rule the gateway applies', async ({ page }) => {
  await login(page)
  const s = suffix()
  const key = await testKey(page)
  const first = await createRule(page, { name: `Browser first ${s}`, condition: marker(s) })
  const second = await createRule(page, { name: `Browser second ${s}`, condition: marker(s) })
  try {
    await expect.poll(() => appliedRule(page, key.secret, s)).toBe(first)
    await ready(page, '/routing-rules')
    await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name: `Browser second ${s}` }).click()
    await page.getByRole('button', { name: 'Check earlier' }).click()
    await expect(page.getByRole('status').filter({ hasText: `Browser second ${s} is now checked` })).toHaveCount(1)
    await expect.poll(() => appliedRule(page, key.secret, s)).toBe(second)
  } finally {
    await remove(page, `/api/routing-rules/${first}`)
    await remove(page, `/api/routing-rules/${second}`)
    await mutate(page, `/api/keys/${key.id}/revoke`)
  }
})
