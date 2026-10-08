import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import { readCookie, XSRF_COOKIE } from '@/api/client'
import type { BffUser, Role } from '@/api/types'

export type AuthStatus = 'unknown' | 'loading' | 'authenticated' | 'unauthenticated' | 'error'

/** Role hierarchy: gateway-admin ⊇ department-admin ⊇ viewer. */
const IMPLIED_ROLES: Record<Role, Role[]> = {
  'gateway-admin': ['gateway-admin', 'department-admin', 'viewer'],
  'department-admin': ['department-admin', 'viewer'],
  viewer: ['viewer'],
}

export function effectiveRoles(roles: readonly string[]): Set<Role> {
  const result = new Set<Role>()
  for (const role of roles) {
    const implied = IMPLIED_ROLES[role as Role]
    if (implied) implied.forEach((r) => result.add(r))
  }
  return result
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<BffUser | null>(null)
  const status = ref<AuthStatus>('unknown')
  let pending: Promise<void> | null = null

  const isAuthenticated = computed(() => status.value === 'authenticated' && !!user.value?.isAuthenticated)
  const roles = computed<string[]>(() => (isAuthenticated.value ? (user.value?.roles ?? []) : []))
  const effective = computed(() => effectiveRoles(roles.value))
  const isDevelopment = computed(() => user.value?.environment === 'Development')
  const departmentCodes = computed(() => user.value?.departmentCodes ?? [])
  const sessionExpiresAt = computed<Date | null>(() => {
    const value = user.value?.sessionExpiresAt
    if (!value) return null
    const date = new Date(value)
    return Number.isNaN(date.getTime()) ? null : date
  })

  function hasRole(role: Role): boolean {
    return effective.value.has(role)
  }

  function hasAnyRole(required: readonly Role[] | undefined): boolean {
    if (!required || required.length === 0) return isAuthenticated.value
    return required.some((r) => hasRole(r))
  }

  const isGatewayAdmin = computed(() => hasRole('gateway-admin'))
  const canManage = computed(() => hasRole('department-admin'))

  /** gateway-admin manages everything; department-admin only the departments in their claim. */
  function canManageDepartment(costCenterCode: string | null | undefined): boolean {
    if (isGatewayAdmin.value) return true
    if (!hasRole('department-admin') || !costCenterCode) return false
    return departmentCodes.value.includes(costCenterCode)
  }

  async function load(): Promise<void> {
    status.value = 'loading'
    try {
      const result = await api.bff.user()
      user.value = result
      status.value = result?.isAuthenticated ? 'authenticated' : 'unauthenticated'
    } catch {
      user.value = null
      status.value = 'error'
    }
  }

  function ensureLoaded(): Promise<void> {
    if (status.value === 'authenticated' || status.value === 'unauthenticated') return Promise.resolve()
    if (!pending) {
      pending = load().finally(() => {
        pending = null
      })
    }
    return pending
  }

  function setUnauthenticated(): void {
    status.value = 'unauthenticated'
    if (user.value) user.value = { ...user.value, isAuthenticated: false }
  }

  /** Explicit, CSRF-protected renewal preserves unsaved forms in this tab. */
  async function extendSession(): Promise<boolean> {
    const result = await api.bff.extendSession()
    if (user.value) user.value = { ...user.value, sessionExpiresAt: result.sessionExpiresAt }
    return isAuthenticated.value
  }

  function loginUrl(returnUrl: string): string {
    const safe = returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/'
    return api.bff.loginUrl(safe)
  }

  async function logout(): Promise<string> {
    const target = await api.bff.logout()
    user.value = null
    status.value = 'unauthenticated'
    return target
  }

  const csrfToken = (): string | null => readCookie(XSRF_COOKIE)

  return {
    user,
    status,
    isAuthenticated,
    roles,
    departmentCodes,
    sessionExpiresAt,
    isDevelopment,
    isGatewayAdmin,
    canManage,
    hasRole,
    hasAnyRole,
    canManageDepartment,
    load,
    ensureLoaded,
    setUnauthenticated,
    extendSession,
    loginUrl,
    logout,
    csrfToken,
  }
})
