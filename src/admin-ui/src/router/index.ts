import { nextTick } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw, type RouterHistory } from 'vue-router'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { ADMIN_ROLES, GATEWAY_ADMIN } from './nav'

declare module 'vue-router' {
  interface RouteMeta {
    /** i18n key for the page title (used by breadcrumbs). */
    titleKey?: string
    /** Roles allowed to open the page. Empty/undefined = any signed-in user. */
    roles?: Role[]
    /** Route name of the breadcrumb parent. */
    parent?: string
    /** Pages that are usable without signing in. */
    public?: boolean
  }
}

export const routes: RouteRecordRaw[] = [
  {
    path: '/',
    name: 'overview',
    component: () => import('@/views/OverviewView.vue'),
    meta: { titleKey: 'nav.overview' },
  },
  {
    path: '/organisation',
    name: 'organisation',
    component: () => import('@/views/OrganisationView.vue'),
    meta: { titleKey: 'nav.organisation', roles: ADMIN_ROLES, parent: 'overview' },
  },
  {
    path: '/organisation/nytt-team',
    name: 'team-wizard',
    component: () => import('@/views/TeamWizardView.vue'),
    meta: { titleKey: 'wizard.title', roles: ADMIN_ROLES, parent: 'organisation' },
  },
  {
    path: '/nycklar',
    name: 'keys',
    component: () => import('@/views/KeysView.vue'),
    meta: { titleKey: 'nav.keys', roles: ADMIN_ROLES, parent: 'overview' },
  },
  {
    path: '/nycklar/ny',
    name: 'key-create',
    component: () => import('@/views/KeyCreateView.vue'),
    meta: { titleKey: 'keys.create.title', roles: ADMIN_ROLES, parent: 'keys' },
  },
  {
    path: '/nycklar/:id',
    name: 'key-detail',
    component: () => import('@/views/KeyDetailView.vue'),
    props: true,
    meta: { titleKey: 'keys.detail.breadcrumb', roles: ADMIN_ROLES, parent: 'keys' },
  },
  {
    path: '/leverantorer',
    name: 'providers',
    component: () => import('@/views/ResourceView.vue'),
    props: { resource: 'providers', titleKey: 'nav.providers' },
    meta: { titleKey: 'nav.providers', roles: GATEWAY_ADMIN, parent: 'overview' },
  },
  {
    path: '/modeller',
    name: 'models',
    component: () => import('@/views/ResourceView.vue'),
    props: { resource: 'models', titleKey: 'nav.models' },
    meta: { titleKey: 'nav.models', roles: GATEWAY_ADMIN, parent: 'overview' },
  },
  {
    path: '/rutter',
    name: 'routes',
    component: () => import('@/views/ResourceView.vue'),
    props: { resource: 'routes', titleKey: 'nav.routes' },
    meta: { titleKey: 'nav.routes', roles: GATEWAY_ADMIN, parent: 'overview' },
  },
  {
    path: '/budgetar',
    name: 'budgets',
    component: () => import('@/views/BudgetsView.vue'),
    meta: { titleKey: 'nav.budgets', roles: ADMIN_ROLES, parent: 'overview' },
  },
  {
    path: '/anvandning',
    name: 'usage',
    component: () => import('@/views/UsageView.vue'),
    meta: { titleKey: 'nav.usage', parent: 'overview' },
  },
  {
    path: '/granskning',
    name: 'audit',
    component: () => import('@/views/AuditView.vue'),
    meta: { titleKey: 'nav.audit', roles: GATEWAY_ADMIN, parent: 'overview' },
  },
  {
    path: '/kom-igang',
    name: 'getting-started',
    component: () => import('@/views/GettingStartedView.vue'),
    meta: { titleKey: 'nav.gettingStarted', parent: 'overview' },
  },
  {
    path: '/modellkatalog',
    name: 'catalog',
    component: () => import('@/views/CatalogView.vue'),
    meta: { titleKey: 'nav.catalog', parent: 'overview' },
  },
  {
    path: '/drift',
    name: 'ops',
    component: () => import('@/views/OpsView.vue'),
    meta: { titleKey: 'nav.ops', roles: GATEWAY_ADMIN, parent: 'overview' },
  },
  {
    path: '/installningar',
    name: 'settings',
    component: () => import('@/views/SettingsView.vue'),
    meta: { titleKey: 'nav.settings', parent: 'overview' },
  },
  {
    path: '/ingen-behorighet',
    name: 'forbidden',
    component: () => import('@/views/ForbiddenView.vue'),
    meta: { titleKey: 'errors.forbiddenTitle', parent: 'overview' },
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { titleKey: 'errors.notFoundTitle', parent: 'overview' },
  },
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
    if (!auth.isAuthenticated || to.meta.public) return true
    if (to.meta.roles && !auth.hasAnyRole(to.meta.roles)) {
      return { name: 'forbidden', query: { from: to.fullPath }, replace: true }
    }
    return true
  })

  let firstNavigation = true
  router.afterEach((to, from, failure) => {
    if (failure) return
    if (firstNavigation) {
      firstNavigation = false
      return
    }
    if (to.path !== from.path) focusPageHeading()
  })

  return router
}
