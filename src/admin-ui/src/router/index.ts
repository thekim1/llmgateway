import { nextTick } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw, type RouterHistory } from 'vue-router'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { ADMIN_ROLES, GATEWAY_ADMIN, KEY_ROLES } from './nav'

declare module 'vue-router' {
  interface RouteMeta {
    /** Page title used for document.title. */
    title?: string
    /** Roles allowed to open the page. Empty/undefined = any signed-in user. */
    roles?: Role[]
    /** Shows usage data: closed to a team admin without a reading role. */
    readsUsage?: boolean
  }
}

export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'overview', component: () => import('@/views/OverviewView.vue'), meta: { title: 'Overview', readsUsage: true } },
  { path: '/usage', name: 'usage', component: () => import('@/views/UsageView.vue'), meta: { title: 'Usage', readsUsage: true } },
  {
    path: '/operations/stability',
    name: 'provider-stability',
    component: () => import('@/views/ProviderStabilityView.vue'),
    meta: { title: 'Provider stability', roles: GATEWAY_ADMIN },
  },
  { path: '/pii-incidents', name: 'pii-incidents', component: () => import('@/views/PiiIncidentsView.vue'), meta: { title: 'PII incidents', readsUsage: true } },
  { path: '/operations', name: 'ops', component: () => import('@/views/OpsView.vue'), meta: { title: 'Health & operations', roles: GATEWAY_ADMIN } },
  { path: '/keys', name: 'keys', component: () => import('@/views/KeysView.vue'), meta: { title: 'Keys', roles: KEY_ROLES } },
  { path: '/organisation', name: 'organisation', component: () => import('@/views/OrganisationView.vue'), meta: { title: 'Organisation', roles: ADMIN_ROLES } },
  { path: '/data-access', name: 'data-access', component: () => import('@/views/DataAccessView.vue'), meta: { title: 'Data API access', roles: GATEWAY_ADMIN } },
  { path: '/catalog', name: 'catalog', component: () => import('@/views/CatalogView.vue'), meta: { title: 'Catalogue' } },
  // Getting started moved to the website (site/); keep old bookmarks working.
  { path: '/getting-started', redirect: { name: 'catalog' } },
  { path: '/routes', name: 'routes', component: () => import('@/views/RoutesView.vue'), meta: { title: 'Routes', roles: GATEWAY_ADMIN } },
  { path: '/routing-rules', name: 'routing-rules', component: () => import('@/views/RoutingRulesView.vue'), meta: { title: 'Routing rules', roles: GATEWAY_ADMIN } },
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

/**
 * Moves focus to a section heading linked with #id (search jumps to cards this way). Data loads
 * after navigation, so the heading may appear a little later; falls back to the page heading.
 */
export function focusSection(hash: string): void {
  const id = decodeURIComponent(hash.slice(1))
  let tries = 0
  const attempt = () => {
    const target = document.getElementById(id)
    if (target) {
      if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1')
      target.classList.add('outline-none')
      target.scrollIntoView({ block: 'start' })
      target.focus({ preventScroll: true })
    } else if (++tries < 20) window.setTimeout(attempt, 100)
    else focusPageHeading()
  }
  void nextTick(() => window.setTimeout(attempt, 0))
}

export function createAppRouter(history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes,
    scrollBehavior(to, _from, saved) {
      // Hash targets are scrolled by focusSection once the section has rendered.
      if (to.hash) return false
      return saved ?? { top: 0 }
    },
  })

  router.beforeEach(async (to) => {
    const auth = useAuthStore()
    await auth.ensureLoaded()
    // Signed-out users see the sign-in page rendered by App.vue regardless of route.
    if (!auth.isAuthenticated) return true
    // A team admin's start page is their keys.
    if (to.name === 'overview' && auth.isTeamAdminOnly) return { name: 'keys', replace: true }
    if (!auth.canOpen(to.meta)) {
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
      if (to.hash) focusSection(to.hash)
      return
    }
    if (to.hash) focusSection(to.hash)
    else if (to.path !== from.path) focusPageHeading()
  })

  return router
}
