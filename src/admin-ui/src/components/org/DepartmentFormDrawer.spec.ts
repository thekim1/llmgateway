import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Department } from '@/api/types'
import DepartmentFormDrawer from './DepartmentFormDrawer.vue'

const create = vi.fn()
const update = vi.fn()
vi.mock('@/stores/departments', () => ({
  useDepartmentsStore: () => ({ create: (...a: unknown[]) => create(...a), update: (...a: unknown[]) => update(...a) }),
}))

const existing: Department = {
  id: 'd1',
  name: 'Individ- och familjeomsorgen',
  costCenterCode: '3000',
  isActive: true,
  teamCount: 0,
  createdAt: '2026-01-01T00:00:00Z',
  memberGroups: ['IFO-medarbetare', '0b6c6c0e-1111-4222-8333-444455556666'],
}

function mountForm(department: Department | null) {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(DepartmentFormDrawer, {
    props: { open: true, department },
    global: { plugins: [pinia], stubs: { UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' } } },
  })
}

describe('DepartmentFormDrawer', () => {
  beforeEach(() => {
    create.mockReset()
    update.mockReset()
  })

  it('shows the member groups one per line and saves them trimmed, without empty lines', async () => {
    update.mockResolvedValue(existing)
    const wrapper = mountForm(existing)
    const groups = wrapper.find('#dept-groups')
    expect((groups.element as HTMLTextAreaElement).value).toBe('IFO-medarbetare\n0b6c6c0e-1111-4222-8333-444455556666')

    await groups.setValue('  IFO-medarbetare \n\nTeknik\n')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).toHaveBeenCalledWith('d1', {
      name: 'Individ- och familjeomsorgen',
      costCenterCode: '3000',
      isActive: true,
      memberGroups: ['IFO-medarbetare', 'Teknik'],
    })
  })

  it('creates a department without member groups as an empty list', async () => {
    create.mockResolvedValue(existing)
    const wrapper = mountForm(null)
    await wrapper.find('#dept-name').setValue('Teknik')
    await wrapper.find('#dept-code').setValue('4000')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(create).toHaveBeenCalledWith({ name: 'Teknik', costCenterCode: '4000', memberGroups: [] })
  })

  it('refuses more than 50 groups before calling the server', async () => {
    const wrapper = mountForm(existing)
    await wrapper.find('#dept-groups').setValue(Array.from({ length: 51 }, (_, i) => `group-${i}`).join('\n'))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter at most 50 groups.')
  })
})
