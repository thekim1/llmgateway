import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  timeout: 180_000,
  expect: { timeout: 15_000 },
  reporter: 'list',
  use: {
    baseURL: process.env.ADMIN_UI_URL ?? 'https://localhost:5173',
    browserName: 'chromium',
    viewport: { width: 1280, height: 900 },
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    ignoreHTTPSErrors: true,
    // Artifacts could capture show-once keys, credentials or request content.
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
})
