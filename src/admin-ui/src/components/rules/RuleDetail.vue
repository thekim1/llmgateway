<script setup lang="ts">
import { computed, ref } from 'vue'
import type { RoutingRule } from '@/api/types'
import { ROUTING_SCOPE_HEADING, ROUTING_SCOPE_LABEL, ROUTING_SCOPE_PLURAL } from '@/utils/labels'
import { targetShares } from '@/utils/routingRules'
import CodeBlock from '../keys/CodeBlock.vue'
import AppIcon from '../ui/AppIcon.vue'
import InlineError from '../ui/InlineError.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'

const props = defineProps<{
  rule: RoutingRule
  /** 1-based place among the rules of this scope, and how many there are. */
  position: number
  count: number
  busy?: boolean
}>()
const emit = defineEmits<{
  (e: 'edit'): void
  (e: 'toggle'): void
  (e: 'delete'): void
  (e: 'reassign'): void
  (e: 'copy'): void
  (e: 'move', delta: -1 | 1): void
}>()

const heading = ref<HTMLElement | null>(null)
defineExpose({ focus: () => heading.value?.focus() })

const shares = computed(() => targetShares(props.rule.targets.map((t) => t.weight)))
const liveOwners = computed(() => props.rule.owners.filter((o) => o.name !== null))
const removedOwners = computed(() => props.rule.owners.length - liveOwners.value.length)
const owner = computed(() => (props.rule.scope === 'Global' ? 'every request' : `its ${ROUTING_SCOPE_PLURAL[props.rule.scope].toLowerCase()}`))
const canMove = computed(() => props.count > 1)
</script>

<template>
  <section aria-labelledby="rule-title" class="flex min-w-0 flex-col gap-4">
    <div class="material flex flex-col gap-6 rounded-card p-6">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex min-w-0 flex-col gap-1">
          <span class="text-small text-fg-3">
            {{ ROUTING_SCOPE_LABEL[rule.scope] }} rule<template v-if="rule.scope !== 'Global'"> · {{ liveOwners.length === 0 ? 'no owner' : `${liveOwners.length} ${(liveOwners.length === 1 ? ROUTING_SCOPE_LABEL[rule.scope] : ROUTING_SCOPE_PLURAL[rule.scope]).toLowerCase()}` }}</template>
          </span>
          <div class="flex flex-wrap items-center gap-2">
            <h2 id="rule-title" ref="heading" tabindex="-1" class="break-words text-title-2 outline-none">{{ rule.name }}</h2>
            <UiBadge v-if="rule.isTemplate" tone="accent" label="Template" :dot="false" />
            <UiBadge v-if="!rule.isEnabled" tone="warn" label="Disabled" />
            <UiBadge v-if="rule.chain" tone="neutral" label="Chain" :dot="false" />
          </div>
          <span v-if="rule.description" class="text-fg-2">{{ rule.description }}</span>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <UiButton v-if="rule.isTemplate" icon="content_copy" @click="emit('copy')">New rule from template</UiButton>
          <UiButton icon="edit" @click="emit('edit')">Edit</UiButton>
          <UiButton
            :icon="rule.isEnabled ? 'block' : 'check_circle'"
            :disabled="busy || (!rule.isEnabled && rule.isOrphaned)"
            :aria-describedby="!rule.isEnabled && rule.isOrphaned ? 'rule-enable-reason' : undefined"
            @click="emit('toggle')"
          >{{ rule.isEnabled ? 'Disable' : 'Enable' }}</UiButton>
          <UiButton icon="account_tree" @click="emit('reassign')">{{ rule.scope === 'Global' ? 'Change scope' : 'Change owners' }}</UiButton>
          <UiButton variant="danger" icon="delete" @click="emit('delete')">Delete</UiButton>
        </div>
      </div>
      <p v-if="!rule.isEnabled && rule.isOrphaned" id="rule-enable-reason" class="-mt-3 text-small text-fg-3">Can’t enable a rule without an owner. Change its owner first.</p>

      <p v-if="rule.isTemplate" class="-mt-3 text-small text-fg-2">
        An example rule, shipped switched off. Edit it and switch it on, or use it as the starting point for a new rule (for example for a few keys).
      </p>

      <div v-if="rule.scope !== 'Global' && rule.owners.length > 0" class="flex flex-col gap-2">
        <h3 class="text-heading">Applies to</h3>
        <ul class="m-0 flex list-none flex-wrap gap-2 p-0" :aria-label="ROUTING_SCOPE_PLURAL[rule.scope]">
          <li v-for="o in liveOwners" :key="o.id" lang="sv" class="rounded-chip bg-surface-2 px-2.5 py-1 text-small">{{ o.name }}</li>
          <li v-if="removedOwners > 0" class="rounded-chip bg-surface-2 px-2.5 py-1 text-small text-fg-3">{{ removedOwners }} removed</li>
        </ul>
      </div>

      <InlineError
        v-if="rule.missingModels.length > 0"
        tone="warn"
        :title="rule.isEnabled ? 'Some models do not exist' : 'Can’t be enabled yet'"
        :message="`${rule.missingModels.join(', ')} ${rule.missingModels.length === 1 ? 'is' : 'are'} not an existing model or route. Create ${rule.missingModels.length === 1 ? 'it' : 'them'} under Routing, or edit the rule to point somewhere else, then switch it on.`"
      />

      <InlineError
        v-if="rule.isOrphaned"
        tone="warn"
        title="This rule has no owner"
        :message="`The ${ROUTING_SCOPE_LABEL[rule.scope].toLowerCase()} it belonged to was removed, so the rule is switched off and never applies. Everything else is kept.`"
      >
        <p class="mt-2"><UiButton size="sm" icon="account_tree" @click="emit('reassign')">Assign a new owner</UiButton></p>
      </InlineError>
      <InlineError v-else-if="rule.isEnabled && rule.validationErrors.length > 0" title="The gateway ignores this rule until it is fixed" message="Edit the rule to fix the problems below, or disable it.">
        <ul class="m-0 list-disc pl-5">
          <li v-for="(problem, i) in rule.validationErrors" :key="i">{{ problem.message }}</li>
        </ul>
      </InlineError>

      <div class="flex flex-col gap-3">
        <h3 class="text-heading">When</h3>
        <CodeBlock v-if="rule.condition.trim()" :code="rule.condition" label="Condition" />
        <p v-else class="text-fg-2">No condition: it matches every request {{ rule.scope === 'Global' ? '' : `from ${owner}` }}.</p>
      </div>

      <div class="flex flex-col gap-3">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h3 class="text-heading">Send to</h3>
          <span v-if="rule.targets.length > 1" class="text-small text-fg-3">Traffic is split by weight; the others take over if one fails.</span>
        </div>
        <ul class="m-0 flex list-none flex-col gap-2 p-0">
          <li v-for="(target, i) in rule.targets" :key="target.model" class="flex flex-col gap-2 rounded-tile bg-surface-2 p-4">
            <div class="flex flex-wrap items-baseline justify-between gap-2">
              <span class="break-all font-mono font-medium">{{ target.model }}</span>
              <span class="tabular text-small text-fg-2">{{ shares[i] }}% · weight {{ target.weight }}</span>
            </div>
            <div class="h-1.5 overflow-hidden rounded-full bg-sunken" aria-hidden="true"><div class="h-full rounded-full bg-chart" :style="{ width: `${shares[i]}%` }" /></div>
          </li>
        </ul>
        <div v-if="rule.fallbacks.length > 0" class="flex flex-col gap-1">
          <h4 class="font-medium">Then, if those fail</h4>
          <ol class="m-0 flex list-none flex-wrap gap-2 p-0">
            <li v-for="(model, i) in rule.fallbacks" :key="model" class="flex items-center gap-1 rounded-chip bg-surface-2 px-2.5 py-1">
              <span class="tabular text-small text-fg-3">{{ i + 1 }}</span><span class="break-all font-mono text-small">{{ model }}</span>
            </li>
          </ol>
        </div>
        <p v-if="rule.chain" class="flex items-start gap-2 text-small text-fg-2">
          <AppIcon name="info" :size="18" class="mt-px" />
          Chain: after this rule the new model name goes through the rules again, so a later rule can route it further.
        </p>
      </div>

      <div class="flex flex-col gap-2">
        <h3 class="text-heading">Order</h3>
        <div class="flex flex-wrap items-center gap-3">
          <span class="text-fg-2">Number <span class="tabular font-medium text-fg">{{ position }} of {{ count }}</span> among the {{ ROUTING_SCOPE_HEADING[rule.scope].toLowerCase() }}<template v-if="rule.scope !== 'Global'">; a request is only checked against the rules of its own {{ ROUTING_SCOPE_LABEL[rule.scope].toLowerCase() }}</template>. The first matching rule wins.</span>
          <span v-if="canMove" class="flex items-center gap-1">
            <UiButton size="sm" icon="arrow_upward" icon-only aria-label="Check earlier" :disabled="busy || position === 1" @click="emit('move', -1)" />
            <UiButton size="sm" icon="arrow_downward" icon-only aria-label="Check later" :disabled="busy || position === count" @click="emit('move', 1)" />
          </span>
        </div>
      </div>
    </div>
  </section>
</template>
