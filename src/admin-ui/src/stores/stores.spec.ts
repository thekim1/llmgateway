import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { CreateKeyRequest, CreateKeyResponse, VirtualKey } from '@/api/types'
import { useKeysStore } from './keys'
import { effectiveRoles, useAuthStore } from './auth'
import { useUiStore } from './ui'
import { THEME_IDS } from '@/theme/themes'

beforeEach(() => { localStorage.clear(); setActivePinia(createPinia()) })
describe('privacy and permissions', () => {
  it('applies role hierarchy without granting unrelated roles', () => {
    expect([...effectiveRoles(['gateway-admin'])]).toEqual(['gateway-admin', 'department-admin', 'viewer'])
    expect([...effectiveRoles(['viewer', 'unknown'])]).toEqual(['viewer'])
  })
  it('scopes department administration to explicit codes', async () => {
    vi.spyOn(api.bff, 'user').mockResolvedValue({ isAuthenticated: true, name: 'User', email: null, roles: ['department-admin'], departmentCodes: ['101'], sessionExpiresAt: null })
    const auth = useAuthStore()
    await auth.load()
    expect(auth.canManageDepartment('101')).toBe(true)
    expect(auth.canManageDepartment('102')).toBe(false)
    expect(auth.isGatewayAdmin).toBe(false)
  })
  it('never stores a newly created key secret in Pinia or localStorage', async () => {
    const key = { id: 'id', name: 'Test', prefix: 'ume-sk-test' } as VirtualKey
    const result: CreateKeyResponse = { key, secret: 'private-test-secret' }
    vi.spyOn(api.keys, 'create').mockResolvedValue(result)
    const keys = useKeysStore()
    const body: CreateKeyRequest = { teamId: 'team', name: 'Test', allowedModels: [], allowedResidencies: [], piiPolicy: 'Block' }
    expect((await keys.create(body)).secret).toBe(result.secret)
    expect(JSON.stringify(keys.$state)).not.toContain(result.secret)
    expect(JSON.stringify(localStorage)).not.toContain(result.secret)
  })
  it.each(THEME_IDS)('persists only display preferences for %s', theme => {
    const ui = useUiStore()
    ui.setTheme(theme)
    ui.setLocale('en')
    expect(document.documentElement.dataset.theme).toBe(theme)
    expect(document.documentElement.lang).toBe('en')
    expect(Object.keys(localStorage).sort()).toEqual(['ume-admin.locale', 'ume-admin.theme'])
  })
  it('surfaces session renewal failure without clearing authenticated state', async () => {
    vi.spyOn(api.bff, 'user').mockResolvedValue({ isAuthenticated: true, name: 'User', email: null, roles: ['viewer'], departmentCodes: ['101'], sessionExpiresAt: '2026-10-07T12:00:00Z' })
    vi.spyOn(api.bff, 'extendSession').mockRejectedValue(new Error('network'))
    const auth = useAuthStore()
    await auth.load()
    await expect(auth.extendSession()).rejects.toThrow('network')
    expect(auth.isAuthenticated).toBe(true)
  })
})
