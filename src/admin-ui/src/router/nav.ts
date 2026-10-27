import type { RouteLocationRaw } from 'vue-router'
import type { Role } from '@/api/types'

/** A place inside a page (a tab or a card) that search can jump straight to. */
export interface NavSection {
  label: string
  to: RouteLocationRaw
  keywords?: string[]
}

export interface NavItem {
  name: string
  label: string
  icon: string
  /** One line on what the page is for; shown in search and as the menu tooltip. */
  description?: string
  /** Extra words people search with that are not in the label, e.g. "circuit" for Operations. */
  keywords?: string[]
  sections?: NavSection[]
  /** Other route names that belong to this page, so it stays highlighted on them. */
  children?: string[]
  roles?: Role[]
  /** Shows usage data, which a team admin without a reading role cannot see. */
  readsUsage?: boolean
  badge?: 'alerts' | 'circuits' | 'update'
}

export interface NavGroup {
  id: string
  label: string
  items: NavItem[]
}

export const ADMIN_ROLES: Role[] = ['gateway-admin', 'department-admin']
export const GATEWAY_ADMIN: Role[] = ['gateway-admin']
/** Team admins manage their own teams' keys, so they see the Keys page too. */
export const KEY_ROLES: Role[] = [...ADMIN_ROLES, 'team-admin']

/**
 * Grouped by the job people come to do: watch traffic, give out access, set up models,
 * control cost and compliance, and change the gateway itself.
 */
export const navGroups: NavGroup[] = [
  {
    id: 'monitor',
    label: 'Monitor',
    items: [
      {
        name: 'overview',
        label: 'Overview',
        icon: 'space_dashboard',
        description: 'Spend, traffic and anything that needs a decision',
        keywords: ['home', 'dashboard', 'attention'],
        readsUsage: true,
      },
      {
        name: 'usage',
        label: 'Usage',
        icon: 'monitoring',
        description: 'Requests, tokens and cost over time',
        keywords: ['traffic', 'tokens', 'statistics', 'export', 'csv'],
        sections: [
          { label: 'Dashboard', to: { name: 'usage' }, keywords: ['charts', 'cache'] },
          { label: 'Requests and costs', to: { name: 'usage', query: { tab: 'requests' } }, keywords: ['request id', 'log', 'cost per request'] },
        ],
        readsUsage: true,
      },
      {
        name: 'ops',
        label: 'Health & operations',
        icon: 'monitor_heart',
        description: 'Component status, provider circuits and maintenance',
        keywords: ['operations', 'status', 'circuit breaker', 'outage', 'cache', 'config export', 'import'],
        sections: [
          { label: 'Provider stability', to: { name: 'provider-stability' }, keywords: ['availability', 'latency', 'p95', 'errors', 'fallback'] },
        ],
        children: ['provider-stability'],
        roles: GATEWAY_ADMIN,
        badge: 'circuits',
      },
    ],
  },
  {
    id: 'access',
    label: 'Access',
    items: [
      {
        name: 'keys',
        label: 'Keys',
        icon: 'key',
        description: 'API keys for applications, with limits and policies',
        keywords: ['api key', 'virtual key', 'rotate', 'revoke', 'token', 'secret'],
        roles: KEY_ROLES,
      },
      {
        name: 'organisation',
        label: 'Organisation',
        icon: 'account_tree',
        description: 'Departments and teams that own keys and budgets',
        keywords: ['department', 'team', 'cost center', 'förvaltning'],
        roles: ADMIN_ROLES,
      },
      {
        name: 'data-access',
        label: 'Data API access',
        icon: 'data_object',
        description: 'Keys for reading usage data from reporting tools',
        keywords: ['power bi', 'reporting', 'export', 'data api'],
        roles: GATEWAY_ADMIN,
      },
    ],
  },
  {
    id: 'models',
    label: 'Models & routing',
    items: [
      {
        name: 'catalog',
        label: 'Catalogue',
        icon: 'menu_book',
        description: 'Models and aliases you can call, with prices',
        keywords: ['models', 'aliases', 'prices', 'connect', 'base url', 'getting started'],
        sections: [
          { label: 'Model aliases', to: { name: 'catalog', hash: '#catalog-routes' } },
          { label: 'Models', to: { name: 'catalog', hash: '#catalog-models' } },
        ],
      },
      {
        name: 'providers',
        label: 'Providers & models',
        icon: 'dns',
        description: 'Provider accounts, the models they serve and prices',
        keywords: ['openai', 'azure', 'foundry', 'ollama', 'vllm', 'endpoint', 'price', 'drain', 'discover'],
        sections: [
          { label: 'Providers', to: { name: 'providers' } },
          { label: 'Models', to: { name: 'providers', query: { show: 'models' } }, keywords: ['model prices', 'context window'] },
        ],
        roles: GATEWAY_ADMIN,
      },
      {
        name: 'routes',
        label: 'Routes',
        icon: 'alt_route',
        description: 'Model names clients call, with fallback and weights',
        keywords: ['alias', 'fallback', 'priority', 'weight', 'load balancing'],
        roles: GATEWAY_ADMIN,
      },
      {
        name: 'routing-rules',
        label: 'Routing rules',
        icon: 'rule',
        description: 'Conditions that send requests to another route',
        keywords: ['conditions', 'redirect', 'policy'],
        roles: GATEWAY_ADMIN,
      },
    ],
  },
  {
    id: 'cost',
    label: 'Cost & compliance',
    items: [
      {
        name: 'budgets',
        label: 'Budgets & alerts',
        icon: 'savings',
        description: 'Spend limits and alerts per department, team and key',
        keywords: ['budget', 'alert', 'limit', 'quota', 'spend'],
        sections: [
          { label: 'Open alerts', to: { name: 'budgets', hash: '#open-alerts-h' } },
          { label: 'Budgets', to: { name: 'budgets', hash: '#budgets-h' } },
        ],
        roles: ADMIN_ROLES,
        badge: 'alerts',
      },
      {
        name: 'pii-incidents',
        label: 'PII incidents',
        icon: 'visibility_off',
        description: 'Requests that contained personal data, and what was done',
        keywords: ['personal data', 'gdpr', 'personnummer', 'masking', 'privacy'],
        readsUsage: true,
      },
      {
        name: 'audit',
        label: 'Audit log',
        icon: 'history',
        description: 'Who changed what, and when',
        keywords: ['history', 'changes', 'actor'],
        roles: GATEWAY_ADMIN,
      },
    ],
  },
  {
    id: 'system',
    label: 'System',
    items: [
      {
        name: 'settings',
        label: 'Settings',
        icon: 'settings',
        description: 'Proxies, retention, logging and version',
        keywords: ['configuration', 'update'],
        sections: [
          { label: 'Trusted proxies', to: { name: 'settings', hash: '#proxies-h' }, keywords: ['client address', 'ip', 'forwarded'] },
          { label: 'Data retention', to: { name: 'settings', hash: '#retention-h' }, keywords: ['delete', 'purge', 'gdpr', 'days'] },
          { label: 'Request logging', to: { name: 'settings', hash: '#request-log-h' }, keywords: ['log', 'prompts'] },
          { label: 'Version', to: { name: 'settings', hash: '#version-h' }, keywords: ['update', 'release', 'upgrade'] },
        ],
        roles: GATEWAY_ADMIN,
        badge: 'update',
      },
    ],
  },
]

/** Bottom bar on phones: monitoring and urgent actions. Everything else is under Menu. */
export const mobileTabs: NavItem[] = [
  { name: 'overview', label: 'Overview', icon: 'space_dashboard', readsUsage: true },
  { name: 'budgets', label: 'Alerts', icon: 'notifications', roles: ADMIN_ROLES, badge: 'alerts' },
  { name: 'keys', label: 'Keys', icon: 'key', roles: KEY_ROLES },
  { name: 'ops', label: 'Health', icon: 'monitor_heart', roles: GATEWAY_ADMIN, badge: 'circuits', children: ['provider-stability'] },
]

/** True when the current route is the item's page or one of its sub-pages. */
export function isNavItemActive(item: NavItem, routeName: unknown): boolean {
  return routeName === item.name || (typeof routeName === 'string' && !!item.children?.includes(routeName))
}

export interface SearchEntry {
  id: string
  label: string
  /** The page a section belongs to; empty for the page itself. */
  parent?: string
  group: string
  icon: string
  description?: string
  to: RouteLocationRaw
  text: string
}

/** Flat list of pages and their sections for search, in menu order. */
export function searchEntries(groups: NavGroup[]): SearchEntry[] {
  return groups.flatMap((g) =>
    g.items.flatMap((item) => {
      const page: SearchEntry = {
        id: item.name,
        label: item.label,
        group: g.label,
        icon: item.icon,
        description: item.description,
        to: { name: item.name },
        text: [item.label, item.description, g.label, ...(item.keywords ?? [])].join(' ').toLowerCase(),
      }
      const sections = (item.sections ?? []).map<SearchEntry>((s, i) => ({
        id: `${item.name}:${i}`,
        label: s.label,
        parent: item.label,
        group: g.label,
        icon: item.icon,
        to: s.to,
        text: [s.label, item.label, ...(s.keywords ?? [])].join(' ').toLowerCase(),
      }))
      return [page, ...sections]
    }),
  )
}

/** Every word of the query must appear; label matches rank first. */
export function filterEntries(entries: SearchEntry[], query: string): SearchEntry[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (!words.length) return entries.filter((e) => !e.parent)
  const matches = entries.filter((e) => words.every((w) => e.text.includes(w)))
  const first = words[0] ?? ''
  const rank = (e: SearchEntry) => (e.label.toLowerCase().startsWith(first) ? 0 : e.label.toLowerCase().includes(first) ? 1 : 2)
  return matches.map((e, i) => ({ e, i, r: rank(e) })).sort((a, b) => a.r - b.r || a.i - b.i).map((x) => x.e)
}
