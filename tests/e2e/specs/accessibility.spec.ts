import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, test } from '@playwright/test'
import { login, pages, ready, testKey, themes } from '../helpers'

const tags = [...new Set(axe.getRules().flatMap(rule => rule.tags))]
  .filter(tag => /^wcag(2|21|22)(a|aa|aaa)$/.test(tag))

test.afterEach(async ({ page }) => { await page.close() })

for (const theme of themes) {
  test(`every routed page passes available WCAG A/AA/AAA axe rules in ${theme}`, async ({ page }) => {
    await login(page)
    const key = await testKey(page)
    await page.evaluate(theme => localStorage.setItem('ume-admin.theme', theme), theme)
    for (const path of [...pages, `/nycklar/${key.id}`]) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      const result = await new AxeBuilder({ page }).withTags(tags).analyze()
      const failures = result.violations.map(v => ({ id: v.id, targets: v.nodes.map(node => node.target) }))
      expect(failures, `${theme}: ${path}`).toEqual([])
      const targets = await page.locator('button:visible, input:visible, select:visible').evaluateAll(elements =>
        elements.filter(element => !element.matches(':disabled')).map(element => {
          const rect = element.getBoundingClientRect()
          return { id: element.id, width: rect.width, height: rect.height }
        }).filter(rect => rect.width < 44 || rect.height < 44))
      expect(targets, `44px controls: ${theme}: ${path}`).toEqual([])
    }
  })
}

test('all pages reflow at 320px and 200 percent text size', async ({ page }) => {
  await login(page)
  const key = await testKey(page)
  for (const width of [320, 640]) {
    await page.setViewportSize({ width, height: 900 })
    for (const path of [...pages, `/nycklar/${key.id}`]) {
      await ready(page, path)
      if (width === 640) await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
      expect(overflow, `${width}px: ${path}`).toBe(false)
    }
  }
})

test('keyboard skip link, route focus, forced colors and reduced motion', async ({ page }) => {
  await login(page)
  await ready(page, '/')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Hoppa till huvudinnehåll' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator('#main')).toBeFocused()
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' })
  await ready(page, '/installningar')
  const focusStyle = await page.locator('#settings-theme').evaluate(element => {
    (element as HTMLElement).focus()
    return getComputedStyle(element).outlineStyle
  })
  expect(focusStyle).not.toBe('none')
  await page.locator('#settings-theme').selectOption('hc-dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'hc-dark')
  await page.locator('#settings-locale').selectOption('en')
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true)
})
