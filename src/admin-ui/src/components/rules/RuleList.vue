<script setup lang="ts">
import type { RoutingRule } from '@/api/types'
import { ROUTING_SCOPE_HEADING } from '@/utils/labels'
import { ownerSummary, type ScopeSection } from '@/utils/routingRules'
import AppIcon from '../ui/AppIcon.vue'
import UiSwitch from '../ui/UiSwitch.vue'

defineProps<{
  sections: ScopeSection[]
  selectedId: string | null
  /** The rule whose switch is waiting for the server. */
  pendingId?: string | null
}>()
defineEmits<{ (e: 'select', id: string): void; (e: 'toggle', rule: RoutingRule, enabled: boolean): void }>()

/** One line of plain text that says what the rule does and anything unusual about it (never colour alone). */
function summary(rule: RoutingRule): string {
  const first = rule.targets[0]?.model ?? 'no target'
  const more = rule.targets.length > 1 ? ` +${rule.targets.length - 1}` : ''
  return [
    `→ ${first}${more}`,
    rule.scope === 'Global' ? null : ownerSummary(rule),
    rule.isTemplate ? 'Template' : null,
    rule.chain ? 'Chain' : null,
    !rule.isEnabled ? 'Off' : null,
    rule.isOrphaned ? 'Needs an owner' : null,
    rule.validationErrors.length > 0 && !rule.isOrphaned ? 'Ignored: invalid' : null,
  ]
    .filter(Boolean)
    .join(' · ')
}

function needsAttention(rule: RoutingRule): boolean {
  return rule.isOrphaned || (rule.isEnabled && rule.validationErrors.length > 0)
}
</script>

<template>
  <nav aria-label="Routing rules" class="flex flex-col gap-5">
    <section v-for="section in sections" :key="section.scope" :aria-labelledby="`rule-section-${section.scope}`" class="flex flex-col gap-2">
      <h2 :id="`rule-section-${section.scope}`" class="px-3 text-caption text-fg-3">{{ ROUTING_SCOPE_HEADING[section.scope] }}</h2>
      <ol class="m-0 flex list-none flex-col gap-1 p-0">
        <li
          v-for="(rule, index) in section.rules"
          :key="rule.id"
          class="flex items-start gap-2 rounded-control pr-3"
          :class="rule.id === selectedId ? 'bg-surface text-fg shadow-1' : 'text-fg-2 hover:bg-hover'"
        >
          <button
            type="button"
            :aria-current="rule.id === selectedId ? 'true' : undefined"
            class="flex min-h-ctl-lg min-w-0 flex-1 items-start gap-3 rounded-control py-2 pl-3 text-left"
            @click="$emit('select', rule.id)"
          >
            <span class="tabular mt-0.5 w-4 flex-none text-small text-fg-3" aria-hidden="true">{{ index + 1 }}</span>
            <span class="flex min-w-0 flex-1 flex-col">
              <span class="break-words font-medium text-fg">{{ rule.name }}</span>
              <span v-if="rule.description" class="line-clamp-2 break-words text-small text-fg-2">{{ rule.description }}</span>
              <span class="break-words text-caption text-fg-3">{{ summary(rule) }}</span>
            </span>
            <span v-if="needsAttention(rule)" class="mt-0.5 flex-none text-warn" role="img" :aria-label="rule.isOrphaned ? 'Has no owner' : 'Is invalid'"><AppIcon name="warning" filled /></span>
          </button>
          <span class="flex-none pt-2.5">
            <UiSwitch
              :model-value="rule.isEnabled"
              :label="`Enable ${rule.name}`"
              :busy="pendingId === rule.id"
              @update:model-value="$emit('toggle', rule, $event)"
            />
          </span>
        </li>
      </ol>
    </section>
  </nav>
</template>
