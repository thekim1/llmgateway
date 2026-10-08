import { nextTick } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw, type RouterHistory } from 'vue-router'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { ADMIN_ROLES, GATEWAY_ADMIN } from './nav'

declare module 'vue-router' {
  interface RouteMeta {
    /** Page title used for document.title. */
    title?: string
    /** Roles allowed to open the page. Empty/undefined = any signed-in user. */
    roles?: Role[]
  }
}

export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'overview', component: () => import('@/views/OverviewView.vue'), meta: { title: 'Overview' } },
  { path: '/usage', name: 'usage', component: () => import('@/views/UsageView.vue'), meta: { title: 'Usage' } },
  { path: '/operations', name: 'ops', component: () => import('@/views/OpsView.vue'), meta: { title: 'Operations', roles: GATEWAY_ADMIN } },
  { path: '/keys', name: 'keys', component: () => import('@/views/KeysView.vue'), meta: { title: 'Keys', roles: ADMIN_ROLES } },
  { path: '/organisation', name: 'organisation', component: () => import('@/views/OrganisationView.vue'), meta: { title: 'Organisation', roles: ADMIN_ROLES } },
  { path: '/catalog', name: 'catalog', component: () => import('@/views/CatalogView.vue'), meta: { title: 'Catalogue' } },
  { path: '/getting-started', name: 'getting-started', component: () => import('@/views/GettingStartedView.vue'), meta: { title: 'Getting started' } },
  { path: '/routes', name: 'routes', component: () => import('@/views/RoutesView.vue'), meta: { title: 'Routes', roles: GATEWAY_ADMIN } },
  { path: '/providers', name: 'providers', component: () => import('@/views/ProvidersView.vue'), meta: { title: 'Providers & models', roles: GATEWAY_ADMIN } },
  { path: '/budgets', name: 'budgets', component: () => import('@/views/BudgetsView.vue'), meta: { title: 'Budgets & alerts', roles: ADMIN_ROLES } },
  { path: '/audit', name: 'audit', component: () => import('@/views/AuditView.vue'), meta: { title: 'Audit log', roles: GATEWAY_ADMIN } },
  { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue'), meta: { title: 'Settings', roles: GATEWAY_ADMIN } },
  { path: '/forbidden', name: 'forbidden', component: () => import('@/views/ForbiddenView.vue'), meta: { title: 'No access' } },
  { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue'), meta: { title: 'Page not found' } },
]

/** Moves focus to the page heading after client-side navigation (not on first load). */
export function focusPageHeading(): void {
  void nextTick(() => {
    window.setTimeout(() => {
      const heading = document.getElementById('page-title')
      if (heading) heading.focus({ preventScroll: false })
      else document.getElementById('main')?.focus()
    }, 0)
  })
}

export function createAppRouter(history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes,
    scrollBehavior(_to, _from, saved) {
      return saved ?? { top: 0 }
    },
  })

  router.beforeEach(async (to) => {
    const auth = useAuthStore()
    await auth.ensureLoaded()
    // Signed-out users see the sign-in page rendered by App.vue regardless of route.
    if (!auth.isAuthenticated) return true
    if (to.meta.roles && !auth.hasAnyRole(to.meta.roles)) {
      return { name: 'forbidden', query: { from: to.fullPath }, replace: true }
    }
    return true
  })

  let firstNavigation = true
  router.afterEach((to, from, failure) => {
    if (failure) return
    document.title = `${to.meta.title ?? 'Admin'} · AI Gateway`
    if (firstNavigation) {
      firstNavigation = false
      return
    }
    if (to.path !== from.path) focusPageHeading()
  })

  return router
}
