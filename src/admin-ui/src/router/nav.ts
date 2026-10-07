import type { Role } from '@/api/types'

export interface NavItem {
  name: string
  labelKey: string
  icon: string
  roles?: Role[]
}

export interface NavGroup {
  id: string
  labelKey: string
  items: NavItem[]
}

export const ADMIN_ROLES: Role[] = ['gateway-admin', 'department-admin']
export const GATEWAY_ADMIN: Role[] = ['gateway-admin']

export const navGroups: NavGroup[] = [
  {
    id: 'admin',
    labelKey: 'nav.groups.admin',
    items: [
      { name: 'overview', labelKey: 'nav.overview', icon: 'dashboard' },
      { name: 'organisation', labelKey: 'nav.organisation', icon: 'account_tree', roles: ADMIN_ROLES },
      { name: 'keys', labelKey: 'nav.keys', icon: 'key', roles: ADMIN_ROLES },
      { name: 'budgets', labelKey: 'nav.budgets', icon: 'savings', roles: ADMIN_ROLES },
      { name: 'usage', labelKey: 'nav.usage', icon: 'monitoring' },
    ],
  },
  {
    id: 'platform',
    labelKey: 'nav.groups.platform',
    items: [
      { name: 'providers', labelKey: 'nav.providers', icon: 'cloud', roles: GATEWAY_ADMIN },
      { name: 'models', labelKey: 'nav.models', icon: 'neurology', roles: GATEWAY_ADMIN },
      { name: 'routes', labelKey: 'nav.routes', icon: 'alt_route', roles: GATEWAY_ADMIN },
      { name: 'ops', labelKey: 'nav.ops', icon: 'monitor_heart', roles: GATEWAY_ADMIN },
      { name: 'audit', labelKey: 'nav.audit', icon: 'history', roles: GATEWAY_ADMIN },
    ],
  },
  {
    id: 'developers',
    labelKey: 'nav.groups.developers',
    items: [
      { name: 'getting-started', labelKey: 'nav.gettingStarted', icon: 'rocket_launch' },
      { name: 'catalog', labelKey: 'nav.catalog', icon: 'menu_book' },
      { name: 'settings', labelKey: 'nav.settings', icon: 'settings' },
    ],
  },
]
