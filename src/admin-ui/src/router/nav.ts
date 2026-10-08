import type { Role } from '@/api/types'

export interface NavItem {
  name: string
  label: string
  icon: string
  roles?: Role[]
  badge?: 'alerts' | 'circuits'
}

export interface NavGroup {
  id: string
  label: string
  items: NavItem[]
}

export const ADMIN_ROLES: Role[] = ['gateway-admin', 'department-admin']
export const GATEWAY_ADMIN: Role[] = ['gateway-admin']

export const navGroups: NavGroup[] = [
  {
    id: 'monitor',
    label: 'Monitor',
    items: [
      { name: 'overview', label: 'Overview', icon: 'space_dashboard' },
      { name: 'usage', label: 'Usage', icon: 'monitoring' },
      { name: 'ops', label: 'Operations', icon: 'monitor_heart', roles: GATEWAY_ADMIN, badge: 'circuits' },
    ],
  },
  {
    id: 'access',
    label: 'Access',
    items: [
      { name: 'keys', label: 'Keys', icon: 'key', roles: ADMIN_ROLES },
      { name: 'organisation', label: 'Organisation', icon: 'account_tree', roles: ADMIN_ROLES },
      { name: 'catalog', label: 'Catalogue', icon: 'menu_book' },
      { name: 'getting-started', label: 'Getting started', icon: 'rocket_launch' },
    ],
  },
  {
    id: 'routing',
    label: 'Routing',
    items: [
      { name: 'routes', label: 'Routes', icon: 'alt_route', roles: GATEWAY_ADMIN },
      { name: 'providers', label: 'Providers & models', icon: 'dns', roles: GATEWAY_ADMIN },
    ],
  },
  {
    id: 'cost',
    label: 'Cost & compliance',
    items: [
      { name: 'budgets', label: 'Budgets & alerts', icon: 'savings', roles: ADMIN_ROLES, badge: 'alerts' },
      { name: 'audit', label: 'Audit log', icon: 'history', roles: GATEWAY_ADMIN },
      { name: 'settings', label: 'Settings', icon: 'settings', roles: GATEWAY_ADMIN },
    ],
  },
]

/** Mobile scope: monitoring and urgent actions only. */
export const mobileTabs: NavItem[] = [
  { name: 'overview', label: 'Overview', icon: 'space_dashboard' },
  { name: 'budgets', label: 'Alerts', icon: 'notifications', roles: ADMIN_ROLES, badge: 'alerts' },
  { name: 'keys', label: 'Keys', icon: 'key', roles: ADMIN_ROLES },
  { name: 'ops', label: 'Health', icon: 'monitor_heart', roles: GATEWAY_ADMIN, badge: 'circuits' },
]
