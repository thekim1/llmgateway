import { expect, test, type Page } from '@playwright/test'
import { appliedRule, confirmDialog, gatewayCall, login, mutate, ready, remove, testKey, testTeam } from '../helpers'

test.afterEach(async ({ page }) => { await page.close() })

const suffix = () => crypto.randomUUID().slice(0, 8)
const marker = (s: string) => `headers["x-e2e"] == "${s}"`

async function createRule(page: Page, body: Record<string, unknown>): Promise<string> {
  const response = await mutate(page, '/api/routing-rules', { targets: [{ model: 'ume/chat-onprem', weight: 1 }], scope: 'Global', ...body })
  expect(response.status(), await response.text()).toBe(201)
  return (await response.json()).id
}

async function rulesNav(page: Page) {
  return page.getByRole('navigation', { name: 'Routing rules' })
}

test('server errors land on the right fields', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  await page.getByRole('button', { name: 'New rule' }).click()
  const drawer = page.getByRole('dialog', { name: 'New rule' })
  const create = drawer.getByRole('button', { name: 'Create rule' })

  // Duplicate name for the same owner: the 409 is shown at the top of the form and the drawer stays open.
  await drawer.getByLabel('Name', { exact: true }).fill('Premium via header')
  await drawer.locator('#rule-target-0').fill('ume/chat-onprem')
  await create.click()
  await expect(drawer.getByText('Det finns redan en regel med samma namn i detta omfång.')).toBeVisible()
  await expect(drawer).toBeVisible()

  // Unknown model: the targets field.
  await drawer.getByLabel('Name', { exact: true }).fill(`Browser rule ${suffix()}`)
  await drawer.locator('#rule-target-0').fill('no/such-model')
  await create.click()
  await expect(drawer.getByText(/Okänd modell eller alias: no\/such-model/)).toBeVisible()

  // A scope without an owner: the owner field.
  await drawer.locator('#rule-target-0').fill('ume/chat-onprem')
  await drawer.getByRole('radio', { name: 'Team' }).click()
  await create.click()
  await expect(drawer.locator('#rule-owner')).toHaveAttribute('aria-invalid', 'true')
  await expect(drawer).toBeVisible()

  // A bad condition reaches the condition field when the server is asked directly.
  const bad = await mutate(page, '/api/routing-rules', { name: `Browser rule ${suffix()}`, scope: 'Global', condition: 'headers["x" ==', targets: [{ model: 'ume/chat-onprem', weight: 1 }] })
  expect(bad.status()).toBe(400)
  expect(Object.keys((await bad.json()).errors)).toContain('condition')
})

test('edit a rule and change its scope', async ({ page }) => {
  await login(page)
  const s = suffix()
  const department = (await testTeam(page))
  const id = await createRule(page, { name: `Browser rule ${s}`, condition: marker(s) })
  try {
    await ready(page, '/routing-rules')
    await (await rulesNav(page)).getByRole('button', { name: `Browser rule ${s}` }).click()
    await page.getByRole('button', { name: 'Edit' }).click()
    const drawer = page.getByRole('dialog', { name: 'Edit rule' })
    await drawer.getByLabel('Name', { exact: true }).fill(`Browser renamed ${s}`)
    await drawer.getByRole('button', { name: 'Save changes' }).click()
    await expect(drawer).toBeHidden()
    await expect(page.getByRole('heading', { level: 2, name: `Browser renamed ${s}`, exact: true })).toBeVisible()

    await page.getByRole('button', { name: 'Change scope' }).click()
    const dialog = page.getByRole('dialog', { name: 'Change owner' })
    await dialog.getByRole('radio', { name: 'Department' }).click()
    const option = dialog.locator('#reassign-owner option', { hasText: department.departmentName })
    await dialog.getByLabel('Department', { exact: true }).selectOption((await option.getAttribute('value'))!)
    await dialog.getByRole('button', { name: 'Assign' }).click()
    await expect(dialog).toBeHidden()
    await expect(page.getByText(/^Department rule · /)).toBeVisible()
    const rule = await (await page.request.get(`/api/routing-rules/${id}`)).json()
    expect(rule.name).toBe(`Browser renamed ${s}`)
    expect(rule.scope).toBe('Department')
    expect(rule.scopeId).toBe(department.departmentId)
  } finally { await remove(page, `/api/routing-rules/${id}`) }
})

test('usage shows the rule that routed a request', async ({ page }) => {
  await login(page)
  const s = suffix()
  const key = await testKey(page)
  const name = `Browser rule ${s}`
  const id = await createRule(page, { name, condition: marker(s) })
  try {
    await expect.poll(async () => (await gatewayCall(page, key.secret, { 'x-e2e': s })).headers()['x-ume-rule']).toBe(id)
    const response = await gatewayCall(page, key.secret, { 'x-e2e': s })
    const requestId = response.headers()['x-request-id']
    await expect.poll(async () => (await page.request.get(`/api/usage/requests/${requestId}`)).status()).toBe(200)
    await ready(page, '/usage')
    await page.getByLabel('Request ID').fill(requestId)
    await page.getByRole('button', { name: 'Look up' }).click()
    const drawer = page.getByRole('dialog')
    await expect(drawer.getByText('Routing rule', { exact: true })).toBeVisible()
    await expect(drawer).toContainText(name)
  } finally {
    await remove(page, `/api/routing-rules/${id}`)
    await mutate(page, `/api/keys/${key.id}/revoke`)
  }
})

for (const role of ['viewer', 'department-admin'] as const) {
  test(`${role} does not see routing rules and is sent to the forbidden page`, async ({ page }) => {
    await login(page, role)
    await expect(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Routing rules' })).toHaveCount(0)
    await page.goto('/routing-rules')
    await expect(page).toHaveURL(/\/forbidden/)
    await expect(page.getByRole('heading', { level: 1, name: 'No access' })).toBeVisible()
    expect((await page.request.get('/api/routing-rules')).status()).toBe(403)
  })
}

test('a key scoped rule keeps working after the key is rotated', async ({ page }) => {
  await login(page)
  const s = suffix()
  const key = await testKey(page)
  const id = await createRule(page, { name: `Browser key rule ${s}`, scope: 'VirtualKey', scopeId: key.id, condition: marker(s) })
  let replacement: string | undefined
  try {
    await expect.poll(() => appliedRule(page, key.secret, s)).toBe(id)
    const rotated = await mutate(page, `/api/keys/${key.id}/rotate`, { mode: 'RevokeImmediately' })
    expect(rotated.status()).toBe(200)
    const body = await rotated.json()
    replacement = body.key.id
    await expect.poll(() => appliedRule(page, body.secret, s)).toBe(id)
  } finally {
    await remove(page, `/api/routing-rules/${id}`)
    if (replacement) await mutate(page, `/api/keys/${replacement}/revoke`)
  }
})

test('budget_used and tokens_used rules read the key\'s own budget and token limit', async ({ page }) => {
  await login(page)
  const s = suffix()
  const withBudget = await testKey(page)
  const plain = await testKey(page)
  const team = (await (await page.request.get('/api/keys/' + withBudget.id)).json()).teamId
  const limited = await mutate(page, '/api/keys', { teamId: team, name: `Browser limited ${s}`, allowedModels: [], allowedResidencies: [], piiPolicy: 'RerouteToOnPrem', tokensPerMinute: 100000 })
  expect(limited.status()).toBe(201)
  const limitedKey = await limited.json()
  const budget = await mutate(page, '/api/budgets', { scope: 'VirtualKey', scopeId: withBudget.id, limitSek: 1000, period: 'Monthly', alertThresholds: [80, 100], isActive: true })
  expect(budget.status()).toBe(201)
  const budgetRule = await createRule(page, { name: `Browser budget rule ${s}`, condition: `budget_used >= 0 && ${marker(s)}` })
  const tokensRule = await createRule(page, { name: `Browser tokens rule ${s}`, condition: `tokens_used >= 0 && ${marker(s + 't')}` })
  try {
    // Only the key with a budget has a budget_used value; without one the comparison does not match.
    await expect.poll(() => appliedRule(page, withBudget.secret, s)).toBe(budgetRule)
    expect(await appliedRule(page, plain.secret, s)).toBeNull()
    // Only the key with a tokens-per-minute limit has a tokens_used value.
    await expect.poll(() => appliedRule(page, limitedKey.secret, s + 't')).toBe(tokensRule)
    expect(await appliedRule(page, plain.secret, s + 't')).toBeNull()
  } finally {
    await remove(page, `/api/routing-rules/${budgetRule}`)
    await remove(page, `/api/routing-rules/${tokensRule}`)
    for (const id of [withBudget.id, plain.id, limitedKey.key.id]) await mutate(page, `/api/keys/${id}/revoke`)
  }
})

test('deleting a department with rules: delete them or keep them switched off, both audited', async ({ page }) => {
  await login(page)
  const s = suffix()
  const make = async (label: string) => {
    const name = `Browser department ${label} ${s}`
    const response = await mutate(page, '/api/departments', { name, costCenterCode: `e2e-${label}-${s}` })
    expect(response.status()).toBe(201)
    const departmentId = (await response.json()).id
    const ruleId = await createRule(page, { name: `Browser ${label} rule ${s}`, scope: 'Department', scopeId: departmentId, condition: marker(s) })
    return { name, departmentId, ruleId }
  }
  const remover = await make('delete')
  const keeper = await make('keep')
  try {
    for (const [department, choice, button] of [[remover, /Delete them/, /Delete department and 1 rule/], [keeper, /Deactivate them/, /Delete department, deactivate 1 rule/]] as const) {
      await ready(page, '/organisation')
      await page.getByRole('navigation', { name: 'Departments' }).getByRole('button', { name: new RegExp(department.name) }).click()
      await page.getByRole('button', { name: 'Delete department' }).click()
      await confirmDialog(page, 'Delete department?', 'Delete department')
      const question = page.getByRole('dialog', { name: /1 routing rule belongs to this department/ })
      await question.getByRole('radio', { name: choice }).check()
      await question.getByRole('button', { name: button }).click()
      await expect(question).toBeHidden()
    }
    expect((await page.request.get(`/api/routing-rules/${remover.ruleId}`)).status()).toBe(404)
    const kept = await (await page.request.get(`/api/routing-rules/${keeper.ruleId}`)).json()
    expect(kept.isEnabled).toBe(false)
    expect(kept.isOrphaned).toBe(true)

    const audit = await (await page.request.get('/api/audit?entityType=RoutingRule&pageSize=100')).json()
    const actions = (id: string) => audit.items.filter((e: { entityId: string }) => e.entityId === id).map((e: { action: string }) => e.action)
    expect(actions(remover.ruleId)).toEqual(expect.arrayContaining(['create', 'delete']))
    expect(actions(keeper.ruleId)).toEqual(expect.arrayContaining(['create', 'deactivate']))
    await ready(page, '/audit')
    await page.getByLabel('Entity type').selectOption('RoutingRule')
    await expect(page.locator('main')).toContainText('deactivate')
  } finally {
    await remove(page, `/api/routing-rules/${keeper.ruleId}`)
  }
})

test('an expired session on the routing rules page sends the user to sign in', async ({ page }) => {
  await login(page)
  await ready(page, '/routing-rules')
  await page.context().clearCookies()
  await page.getByRole('button', { name: 'Test a request' }).click()
  const dialog = page.getByRole('dialog', { name: 'Test a request' })
  await dialog.getByLabel('Model the client asks for').fill('ume/chat-standard')
  await dialog.getByRole('button', { name: 'Run test' }).click()
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible()
})
