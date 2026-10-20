import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { VersionInfo } from '@/api/types'
import VersionCard from './VersionCard.vue'

const version = vi.fn()
vi.mock('@/api', () => ({
  api: { settings: { version: (...a: unknown[]) => version(...a) } },
}))

const info = (overrides: Partial<VersionInfo> = {}): VersionInfo => ({
  version: '0.6.0',
  developmentBuild: false,
  checkEnabled: true,
  latest: { version: '0.6.1', name: 'Ume LLM Gateway 0.6.1', url: 'https://github.com/thekim1/llmgateway/releases/tag/v0.6.1', publishedAt: '2026-10-18T18:57:01Z' },
  updateAvailable: true,
  checkedAt: '2026-10-20T08:00:00Z',
  checkFailed: false,
  ...overrides,
})

async function mountCard(initial: VersionInfo) {
  setActivePinia(createPinia())
  version.mockResolvedValue(initial)
  const wrapper = mount(VersionCard)
  await flushPromises()
  return wrapper
}

beforeEach(() => version.mockReset())

describe('VersionCard', () => {
  it('shows the running version and links the newer release', async () => {
    const wrapper = await mountCard(info())
    expect(wrapper.text()).toContain('0.6.0')
    expect(wrapper.text()).toContain('Version 0.6.1 is available')
    expect(wrapper.text()).toContain('Update available')
    const links = wrapper.findAll('a').map((a) => a.attributes('href'))
    expect(links).toContain('https://github.com/thekim1/llmgateway/releases/tag/v0.6.1')
  })

  it('says up to date when the latest release is running', async () => {
    const wrapper = await mountCard(info({ version: '0.6.1', updateAvailable: false }))
    expect(wrapper.text()).toContain('Up to date')
    expect(wrapper.text()).not.toContain('is available')
  })

  it('marks a development build and never offers an update', async () => {
    const wrapper = await mountCard(info({ version: '0.0.0-local', developmentBuild: true, updateAvailable: false }))
    expect(wrapper.text()).toContain('Development build')
    expect(wrapper.text()).not.toContain('is available')
  })

  it('explains a switched-off or failed check', async () => {
    const off = await mountCard(info({ checkEnabled: false, latest: null, updateAvailable: false, checkedAt: null }))
    expect(off.text()).toContain('Admin:CheckForReleases')

    const failed = await mountCard(info({ latest: null, updateAvailable: false, checkFailed: true }))
    expect(failed.text()).toContain('Could not reach GitHub')
    expect(failed.text()).toContain('retried within 30 minutes')
  })
})
