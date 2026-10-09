import { expect, test, type Page } from '@playwright/test'
import { appliedRule, confirmDialog, login, mutate, ready, remove, testKey, testTeam } from '../helpers'

test.afterEach(async ({ page }) => { await page.close() })

async function selectRule(page: Page, name: string): Promise<void> {
  await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name }).click()
  await expect(page.getByRole('heading', { level: 2, name, exact: true })).toBeVisible()
}

test('create, validate, test, reorder, disable and delete a rule, and see it apply to a real request', async ({ page }) => {
  await login(page)
  const suffix = crypto.randomUUID().slice(0, 8)
  const name = `Browser rule ${suffix}`
  const condition = `headers["x-e2e"] == "${suffix}"`
  const key = await testKey(page)
  let ruleId: string | undefined
  try {
    await ready(page, '/routing-rules')
    await page.getByRole('button', { name: 'New rule' }).click()
    const drawer = page.getByRole('dialog', { name: 'New rule' })
    await drawer.getByLabel('Name', { exact: true }).fill(name)

    // Live validation: a broken condition is explained in the live region, a fixed one is confirmed.
    await drawer.locator('#rule-condition').fill('headers["x-e2e" ==')
    await expect(drawer.getByRole('status').filter({ hasText: /character \d+/ })).toBeVisible()
    await drawer.locator('#rule-condition').fill(condition)
    await expect(drawer.getByRole('status').filter({ hasText: 'Valid' })).toBeVisible()

    await drawer.locator('#rule-target-0').fill('ume/chat-onprem')
    const created = page.waitForResponse(r => r.url().endsWith('/api/routing-rules') && r.request().method() === 'POST')
    await drawer.getByRole('button', { name: 'Create rule' }).click()
    const rule = await (await created).json()
    ruleId = rule.id
    await expect(drawer).toBeHidden()
    await expect(page.getByRole('heading', { level: 2, name, exact: true })).toBeVisible()

    // Test a request: the rule applies to a request with the marker header and is not chosen without it.
    await page.getByRole('button', { name: 'Test a request' }).click()
    const test = page.getByRole('dialog', { name: 'Test a request' })
    await test.getByLabel('Model the client asks for').fill('ume/chat-standard')
    await test.getByText('Headers and request parameters').click()
    await test.getByRole('button', { name: 'Add header' }).click()
    await test.getByLabel('Header 1 name').fill('x-e2e')
    await test.getByLabel('Header 1 value').fill(suffix)
    await test.getByRole('button', { name: 'Run test' }).click()
    await expect(test.getByRole('heading', { name: `${name} applies` })).toBeVisible()
    await test.getByLabel('Header 1 value').fill('something-else')
    await test.getByRole('button', { name: 'Run test' }).click()
    await expect(test.getByRole('heading', { name: /applies|No rule matches/ })).not.toContainText(name)
    await test.getByRole('button', { name: 'Close', exact: true }).click()
    await expect(test).toBeHidden()

    // The real gateway applies the rule and says which one in x-ume-rule.
    await expect.poll(() => appliedRule(page, key.secret, suffix)).toBe(ruleId)
    expect(await appliedRule(page, key.secret, 'not-the-marker')).toBeNull()

    // Move it earlier and announce the new position.
    const before = await page.getByText(/Checked \d+ of \d+/).first().textContent()
    await page.getByRole('button', { name: 'Check earlier' }).click()
    await expect(page.getByRole('status').filter({ hasText: `${name} is now checked` })).toHaveCount(1)
    expect(await page.getByText(/Checked \d+ of \d+/).first().textContent()).not.toBe(before)

    // Disable: the gateway stops applying it. Enable: it applies again.
    await page.getByRole('button', { name: 'Disable', exact: true }).click()
    await expect(page.getByText('Disabled', { exact: true })).toBeVisible()
    await expect.poll(() => appliedRule(page, key.secret, suffix)).toBeNull()
    await page.getByRole('button', { name: 'Enable', exact: true }).click()
    await expect.poll(() => appliedRule(page, key.secret, suffix)).toBe(ruleId)

    // Delete.
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirmDialog(page, 'Delete rule?', 'Delete rule')
    await expect(page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name })).toHaveCount(0)
    await expect.poll(() => appliedRule(page, key.secret, suffix)).toBeNull()
    ruleId = undefined
  } finally {
    if (ruleId) await remove(page, `/api/routing-rules/${ruleId}`)
    await mutate(page, `/api/keys/${key.id}/revoke`)
  }
})

test('deleting a team keeps its rules switched off until they are given a new owner', async ({ page }) => {
  await login(page)
  const suffix = crypto.randomUUID().slice(0, 8)
  const name = `Browser team rule ${suffix}`
  const oldTeam = await testTeam(page)
  const newTeam = await testTeam(page)
  const keyResponse = await mutate(page, '/api/keys', {
    teamId: newTeam.teamId, name: `Browser key ${suffix}`, allowedModels: [], allowedResidencies: [], piiPolicy: 'RerouteToOnPrem',
  })
  expect(keyResponse.status()).toBe(201)
  const key = await keyResponse.json()
  const ruleResponse = await mutate(page, '/api/routing-rules', {
    name, scope: 'Team', scopeId: oldTeam.teamId, condition: `headers["x-e2e"] == "${suffix}"`, targets: [{ model: 'ume/chat-onprem', weight: 1 }],
  })
  expect(ruleResponse.status()).toBe(201)
  const rule = await ruleResponse.json()
  try {
    // Delete the old team and choose "Deactivate".
    await ready(page, '/organisation')
    await page.getByRole('navigation', { name: 'Departments' }).getByRole('button', { name: new RegExp(oldTeam.departmentName) }).click()
    await page.getByRole('row').filter({ hasText: oldTeam.teamName }).getByRole('button', { name: 'Delete' }).click()
    await confirmDialog(page, 'Delete team?', 'Delete team')
    const choice = page.getByRole('dialog', { name: /1 routing rule belongs to this team/ })
    await expect(choice).toBeVisible()
    await expect(choice.getByRole('radio', { name: /Deactivate them/ })).toBeChecked()
    await choice.getByRole('button', { name: /Delete team, deactivate 1 rule/ }).click()
    await expect(choice).toBeHidden()

    // The rule is under "Needs owner" and the gateway does not apply it.
    await ready(page, '/routing-rules')
    await page.getByRole('button', { name: /^Needs owner/ }).first().click()
    await selectRule(page, name)
    await expect(page.getByText('This rule has no owner')).toBeVisible()
    await expect(page.getByText('Disabled', { exact: true })).toBeVisible()
    expect(await appliedRule(page, key.secret, suffix)).toBeNull()

    // Give it to the other team: it is enabled and applies again.
    await page.getByRole('button', { name: 'Change owner' }).click()
    const reassign = page.getByRole('dialog', { name: 'Assign a new owner' })
    await reassign.getByRole('radio', { name: 'Team' }).click()
    const option = reassign.locator('#reassign-owner option', { hasText: newTeam.teamName })
    await reassign.getByLabel('Team', { exact: true }).selectOption((await option.getAttribute('value'))!)
    await reassign.getByRole('button', { name: 'Assign' }).click()
    await expect(reassign).toBeHidden()
    await expect(page.getByText('This rule has no owner')).toHaveCount(0)
    await expect.poll(() => appliedRule(page, key.secret, suffix)).toBe(rule.id)
  } finally {
    await remove(page, `/api/routing-rules/${rule.id}`)
    await mutate(page, `/api/keys/${key.key.id}/revoke`)
  }
})
