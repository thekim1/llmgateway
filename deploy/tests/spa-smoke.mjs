import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { strict as assert } from 'node:assert'
import { execFileSync } from 'node:child_process'
import { createHmac } from 'node:crypto'

const require = createRequire(resolve('tests', 'e2e', 'package.json'))
const { chromium } = require('@playwright/test')
const browser = await chromium.launch()
try {
  const page = await browser.newPage({ ignoreHTTPSErrors: true })
  const errors = []
  page.on('pageerror', () => errors.push('JavaScript runtime error'))
  page.on('console', message => { if (message.type() === 'error') errors.push('Browser console error') })
  await page.addInitScript(() => {
    window.__cspViolations = []
    document.addEventListener('securitypolicyviolation', event => window.__cspViolations.push(event.violatedDirective))
  })
  await page.goto('https://localhost:19443/settings')
  await page.getByRole('link', { name: 'Sign in', exact: true }).waitFor()
  await page.evaluate(() => document.fonts.ready)
  assert.deepEqual(errors, [])
  assert.deepEqual(await page.evaluate(() => window.__cspViolations), [])
  assert.ok((await page.locator('link[rel="stylesheet"]').count()) > 0)
  const unsigned = await page.request.get('https://localhost:18443/health/operations')
  assert.equal(unsigned.status(), 401)
  const pepper = execFileSync('docker', ['exec', process.argv[2], 'cat', '/workspace/secrets/gateway/Security__KeyPepper'])
  const timestamp = Math.floor(Date.now() / 1000).toString()
  const signature = createHmac('sha256', pepper).update(`Ume.LlmGateway.Operations.v1\nGET\n/health/operations\n${timestamp}`).digest('hex').toUpperCase()
  pepper.fill(0)
  const response = await page.request.get('https://localhost:18443/health/operations', {
    headers: { 'x-ume-ops-timestamp': timestamp, 'x-ume-ops-signature': signature },
  })
  assert.equal(response.status(), 200)
  const status = await response.json()
  assert.equal(status.status, 'Healthy')
  assert.match(status.version, /^\d+\.\d+\.\d+/)
  assert.equal(status.usageWriter.capacity, 10000)
  assert.equal(status.usageWriter.consecutiveFailures, 0)
  console.log('Production SPA assets/CSP and authenticated live gateway version/queue verified in Chromium.')
  await page.close()
} finally {
  await browser.close()
}
