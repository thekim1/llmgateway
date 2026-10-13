import { expect, test } from '@playwright/test'
import { login, openRow, ready, remove, testKey } from '../helpers'

test.afterEach(async ({ page }) => { await page.close() })

// Registers a second copy of the seeded demo MCP server (FakeLlm's /mcp), syncs it, approves one tool, grants it to a
// fresh key and calls it through the gateway.
test('MCP server: register, sync, approve, grant and call', async ({ page }) => {
  await login(page)
  const servers = await (await page.request.get('/api/mcp/servers')).json()
  const demo = servers.find((s: { slug: string }) => s.slug === 'demo')
  expect(demo, 'the dev seed adds the demo MCP server').toBeTruthy()
  const slug = `e2e_${crypto.randomUUID().replace(/-/g, '').slice(0, 10)}`
  const key = await testKey(page)

  await ready(page, '/mcp-servers')
  await page.getByRole('button', { name: 'New MCP server' }).click()
  const drawer = page.getByRole('dialog', { name: 'New MCP server' })
  await drawer.getByLabel('Short name').fill('Bad__Name')
  await expect(drawer.getByText('Use lower-case letters and digits')).toBeVisible()
  await drawer.getByLabel('Short name').fill(slug)
  await drawer.getByLabel('Display name').fill(`Browser MCP ${slug}`)
  await drawer.getByLabel('URL', { exact: true }).fill(demo.url)
  await drawer.getByRole('button', { name: 'Test connection' }).click()
  await expect(drawer.getByTestId('mcp-test-result')).toContainText('Connected.')
  await drawer.getByRole('button', { name: 'Create server' }).click()

  const serverDrawer = page.getByRole('dialog', { name: `Browser MCP ${slug}` })
  await serverDrawer.getByRole('button', { name: 'Sync now' }).click()
  await expect(serverDrawer.getByTestId('mcp-sync-result')).toContainText('3 new tools')
  await serverDrawer.getByRole('row').filter({ hasText: 'echo' }).getByRole('button').first().click()
  const review = page.getByRole('dialog', { name: 'Review echo' })
  await expect(review).toContainText(`${slug}__echo`)
  await review.getByRole('button', { name: 'Approve', exact: true }).click()
  await expect(review).toBeHidden()
  await expect(serverDrawer.getByRole('row').filter({ hasText: 'echo' })).toContainText('Approved')
  await serverDrawer.getByRole('button', { name: 'Close' }).click()

  await ready(page, '/keys')
  const keyName = (await (await page.request.get(`/api/keys/${key.id}`)).json()).name
  await openRow(page, keyName)
  const detail = page.getByRole('dialog', { name: keyName })
  await detail.getByRole('button', { name: 'Edit MCP access' }).click()
  await detail.getByLabel('Add MCP server').selectOption({ label: `Browser MCP ${slug} (${slug})` })
  await detail.getByRole('button', { name: 'Add', exact: true }).click()
  await detail.getByRole('button', { name: 'Save MCP access' }).click()
  await expect(detail.getByTestId('key-mcp-grants')).toContainText('All approved tools')

  const catalogue = await (await page.request.get('/api/catalog')).json()
  const call = async (method: string, params: object) =>
    page.request.post(`${catalogue.gatewayBaseUrl}/mcp/${slug}`, {
      headers: { Authorization: `Bearer ${key.secret}` },
      data: { jsonrpc: '2.0', id: 1, method, params },
    })
  await expect.poll(async () => (await (await call('tools/list', {})).json()).result?.tools?.map((t: { name: string }) => t.name)).toEqual(['echo'])
  const result = await (await call('tools/call', { name: 'echo', arguments: { text: 'hej' } })).json()
  expect(result.result.content[0].text).toBe('hej')

  const created = (await (await page.request.get('/api/mcp/servers')).json()).find((s: { slug: string }) => s.slug === slug)
  expect((await remove(page, `/api/mcp/servers/${created.id}`)).status()).toBe(204)
})
