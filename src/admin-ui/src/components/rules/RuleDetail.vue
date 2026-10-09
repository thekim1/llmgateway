<script setup lang="ts">
import { computed, ref } from 'vue'
import type { RoutingRule } from '@/api/types'
import { ROUTING_SCOPE_LABEL } from '@/utils/labels'
import { targetShares } from '@/utils/routingRules'
import CodeBlock from '../keys/CodeBlock.vue'
import AppIcon from '../ui/AppIcon.vue'
import InlineError from '../ui/InlineError.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'

const props = defineProps<{
  rule: RoutingRule
  /** 1-based place among the owner's rules in this scope, and how many there are. */
  position: number
  count: number
  busy?: boolean
}>()
const emit = defineEmits<{
  (e: 'edit'): void
  (e: 'toggle'): void
  (e: 'delete'): void
  (e: 'reassign'): void
  (e: 'move', delta: -1 | 1): void
}>()

const heading = ref<HTMLElement | null>(null)
defineExpose({ focus: () => heading.value?.focus() })

const shares = computed(() => targetShares(props.rule.targets.map((t) => t.weight)))
const owner = computed(() => (props.rule.scope === 'Global' ? 'every request' : props.rule.scopeName))
const canMove = computed(() => props.count > 1)
</script>

<template>
  <section aria-labelledby="rule-title" class="flex min-w-0 flex-col gap-4">
    <div class="material flex flex-col gap-6 rounded-card p-6">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex min-w-0 flex-col gap-1">
          <span class="text-small text-fg-3">
            {{ ROUTING_SCOPE_LABEL[rule.scope] }} rule<template v-if="rule.scope !== 'Global'"> · <span v-if="rule.scopeName" lang="sv">{{ rule.scopeName }}</span><template v-else>no owner</template></template>
          </span>
          <div class="flex flex-wrap items-center gap-2">
            <h2 id="rule-title" ref="heading" tabindex="-1" class="break-words text-title-2 outline-none">{{ rule.name }}</h2>
            <UiBadge v-if="!rule.isEnabled" tone="warn" label="Disabled" />
            <UiBadge v-if="rule.chain" tone="neutral" label="Chain" :dot="false" />
          </div>
          <span v-if="rule.description" class="text-fg-2">{{ rule.description }}</span>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <UiButton icon="edit" @click="emit('edit')">Edit</UiButton>
          <UiButton
            :icon="rule.isEnabled ? 'block' : 'check_circle'"
            :disabled="busy || (!rule.isEnabled && rule.isOrphaned)"
            :aria-describedby="!rule.isEnabled && rule.isOrphaned ? 'rule-enable-reason' : undefined"
            @click="emit('toggle')"
          >{{ rule.isEnabled ? 'Disable' : 'Enable' }}</UiButton>
          <UiButton icon="account_tree" @click="emit('reassign')">{{ rule.scope === 'Global' ? 'Change scope' : 'Change owner' }}</UiButton>
          <UiButton variant="danger" icon="delete" @click="emit('delete')">Delete</UiButton>
        </div>
      </div>
      <p v-if="!rule.isEnabled && rule.isOrphaned" id="rule-enable-reason" class="-mt-3 text-small text-fg-3">Can’t enable a rule without an owner. Change its owner first.</p>

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
          <span class="text-fg-2">Checked <span class="tabular font-medium text-fg">{{ position }} of {{ count }}</span><template v-if="rule.scope !== 'Global'"> for <span v-if="rule.scopeName" lang="sv">{{ rule.scopeName }}</span><template v-else>this owner</template></template>. The first matching rule wins.</span>
          <span v-if="canMove" class="flex items-center gap-1">
            <UiButton size="sm" icon="arrow_upward" icon-only aria-label="Check earlier" :disabled="busy || position === 1" @click="emit('move', -1)" />
            <UiButton size="sm" icon="arrow_downward" icon-only aria-label="Check later" :disabled="busy || position === count" @click="emit('move', 1)" />
          </span>
        </div>
      </div>
    </div>
  </section>
</template>
