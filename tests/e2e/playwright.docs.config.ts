import { defineConfig } from '@playwright/test'

// Regenerates the screenshots in docs/images. Not part of the normal suite: run it against a stack from scripts/Start-E2E.ps1 with
//   npx playwright test -c playwright.docs.config.ts
export default defineConfig({
  testDir: './docs',
  workers: 1,
  timeout: 180_000,
  expect: { timeout: 15_000 },
  reporter: 'list',
  use: {
    baseURL: process.env.ADMIN_UI_URL ?? 'https://localhost:5173',
    browserName: 'chromium',
    viewport: { width: 1280, height: 1400 },
    deviceScaleFactor: 1,
    colorScheme: 'light',
    ignoreHTTPSErrors: true,
    trace: 'off',
    video: 'off',
  },
})
