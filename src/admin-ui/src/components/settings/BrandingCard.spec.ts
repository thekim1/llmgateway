import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Branding } from '@/api/types'
import { inlineOverlay } from '@/test/fixtures'
import { button } from '@/test/helpers'
import { useBrandingStore } from '@/stores/branding'
import BrandingCard from './BrandingCard.vue'

const branding = vi.fn()
const updateBranding = vi.fn()
const uploadLogo = vi.fn()
const removeLogo = vi.fn()
vi.mock('@/api', () => ({
  api: {
    bff: { branding: (...a: unknown[]) => branding(...a) },
    settings: {
      updateBranding: (...a: unknown[]) => updateBranding(...a),
      uploadLogo: (...a: unknown[]) => uploadLogo(...a),
      removeLogo: (...a: unknown[]) => removeLogo(...a),
    },
  },
}))

const value = (overrides: Partial<Branding> = {}): Branding => ({
  organisationName: 'Umeå kommun',
  logoUrl: null,
  updatedAt: null,
  ...overrides,
})

async function mountCard(initial: Branding) {
  setActivePinia(createPinia())
  branding.mockResolvedValue(initial)
  const wrapper = mount(BrandingCard, { global: { stubs: { UiDialog: inlineOverlay } } })
  await flushPromises()
  return wrapper
}

function choose(wrapper: Awaited<ReturnType<typeof mountCard>>, file: File) {
  const input = wrapper.find('input[type="file"]')
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  return input.trigger('change')
}

beforeEach(() => {
  for (const mock of [branding, updateBranding, uploadLogo, removeLogo]) mock.mockReset()
})

describe('BrandingCard', () => {
  it('shows the current name and the built-in icon without a logo', async () => {
    const wrapper = await mountCard(value())
    expect((wrapper.find('#branding-name').element as HTMLInputElement).value).toBe('Umeå kommun')
    expect(wrapper.text()).toContain('The built-in icon')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('Upload logo…')
  })

  it('saves a trimmed name and shows it in the menu at once', async () => {
    const wrapper = await mountCard(value())
    await wrapper.find('#branding-name').setValue('  Example kommun ')
    updateBranding.mockResolvedValue(value({ organisationName: 'Example kommun', updatedAt: '2026-11-07T10:00:00Z' }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(updateBranding).toHaveBeenCalledWith('Example kommun')
    expect(useBrandingStore().organisationName).toBe('Example kommun')
    expect(wrapper.text()).toContain('Last changed')
  })

  it('refuses a name over 80 characters without calling the server', async () => {
    const wrapper = await mountCard(value())
    await wrapper.find('#branding-name').setValue('x'.repeat(81))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(updateBranding).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('at most 80 characters')
  })

  it('uploads a logo as base64 and shows it', async () => {
    const wrapper = await mountCard(value())
    uploadLogo.mockResolvedValue(value({ logoUrl: '/bff/branding/logo?v=abc' }))
    await choose(wrapper, new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'logo.png', { type: 'image/png' }))
    await vi.waitFor(() => expect(uploadLogo).toHaveBeenCalledWith('iVBORw=='))
    await flushPromises()
    expect(wrapper.find('img').attributes('src')).toBe('/bff/branding/logo?v=abc')
    expect(wrapper.text()).toContain('Replace logo…')
  })

  it('refuses other file types and large images before uploading', async () => {
    const wrapper = await mountCard(value())
    await choose(wrapper, new File(['GIF89a'], 'logo.gif', { type: 'image/gif' }))
    await flushPromises()
    expect(wrapper.text()).toContain('Choose a PNG, JPEG, WebP or SVG image.')

    await choose(wrapper, new File([new Uint8Array(256 * 1024 + 1)], 'big.png', { type: 'image/png' }))
    await flushPromises()
    expect(wrapper.text()).toContain('the limit is 256 KB')
    expect(uploadLogo).not.toHaveBeenCalled()
  })

  it('removes the logo after confirming', async () => {
    const wrapper = await mountCard(value({ logoUrl: '/bff/branding/logo?v=abc' }))
    await button(wrapper, 'Remove logo…').trigger('click')
    await flushPromises()
    removeLogo.mockResolvedValue(value())
    await wrapper.findAll('button').find((b) => b.text() === 'Remove')!.trigger('click')
    await flushPromises()
    expect(removeLogo).toHaveBeenCalled()
    expect(wrapper.find('img').exists()).toBe(false)
  })
})
