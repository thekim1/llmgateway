import type { RoutingRule } from '@/api/types'

let counter = 0

/** A valid, enabled global routing rule; override what the test cares about. */
export function rule(overrides: Partial<RoutingRule> = {}): RoutingRule {
  counter += 1
  return {
    id: `r${counter}`,
    name: `Rule ${counter}`,
    description: null,
    isEnabled: true,
    priority: counter * 10,
    scope: 'Global',
    scopeId: null,
    scopeName: null,
    isOrphaned: false,
    condition: '',
    chain: false,
    targets: [{ model: 'ume/chat-advanced', weight: 1 }],
    fallbacks: [],
    validationErrors: [],
    createdAt: '2026-10-09T08:00:00Z',
    updatedAt: '2026-10-09T08:00:00Z',
    ...overrides,
  }
}

/** Stub for the Reka UI drawer/dialog wrappers: renders the slots inline when open, so tests need no portal. */
export const inlineOverlay = {
  props: ['open', 'title'],
  template: '<div v-if="open"><h2>{{ title }}</h2><slot name="description" /><slot /><slot name="actions" /><slot name="footer" /></div>',
}
