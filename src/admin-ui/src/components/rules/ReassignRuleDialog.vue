<script setup lang="ts">
import { ref, watch } from 'vue'
import type { RoutingRule, RoutingScope } from '@/api/types'
import { useRuleOwners } from '@/composables/useRuleOwners'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useUiStore } from '@/stores/ui'
import { ROUTING_SCOPE_HINT, ROUTING_SCOPE_LABEL, ROUTING_SCOPE_PLURAL } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import CheckField from '../ui/CheckField.vue'
import InlineError from '../ui/InlineError.vue'
import SegmentedControl from '../ui/SegmentedControl.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'
import OwnerPicker from './OwnerPicker.vue'

const props = defineProps<{ open: boolean; rule: RoutingRule | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const rules = useRoutingRulesStore()
const ui = useUiStore()

const scope = ref<RoutingScope>('Team')
const scopeIds = ref<string[]>([])
const enable = ref(true)
const busy = ref(false)
const error = ref<unknown>(null)
const ownerError = ref<string | null>(null)

const SCOPE_ORDER: RoutingScope[] = ['Global', 'Department', 'Team', 'VirtualKey']
const scopeOptions = SCOPE_ORDER.map((s) => ({ value: s, label: ROUTING_SCOPE_LABEL[s] }))
const owners = useRuleOwners(scope)

function setScope(next: RoutingScope): void {
  scope.value = next
  scopeIds.value = []
  ownerError.value = null
  void owners.load(next)
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    error.value = null
    ownerError.value = null
    scope.value = props.rule && props.rule.scope !== 'Global' ? props.rule.scope : 'Team'
    // Keep the owners that still exist, so adding one more is a single tick.
    scopeIds.value = props.rule?.scope === scope.value ? props.rule.owners.filter((o) => o.name !== null).map((o) => o.id) : []
    enable.value = true
    void owners.load(scope.value)
  },
)

async function submit(): Promise<void> {
  if (!props.rule || busy.value) return
  error.value = null
  ownerError.value = null
  if (scope.value !== 'Global' && scopeIds.value.length === 0) {
    ownerError.value = `Choose at least one ${ROUTING_SCOPE_LABEL[scope.value].toLowerCase()} this rule should apply to.`
    document.getElementById('reassign-owner')?.focus()
    return
  }
  busy.value = true
  try {
    await rules.reassign(props.rule.id, { scope: scope.value, scopeIds: scope.value === 'Global' ? [] : [...scopeIds.value], enable: enable.value })
    ui.notify(enable.value ? 'Rule assigned and enabled' : 'Rule assigned')
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog :open="open" :title="rule?.isOrphaned ? 'Assign a new owner' : 'Change owners'" icon="account_tree" @update:open="emit('update:open', $event)">
    <template #description>
      <span class="font-medium">{{ rule?.name }}</span> keeps its condition, targets and order. Only who it applies to changes.
    </template>
    <form id="reassign-form" class="flex flex-col gap-4" novalidate @submit.prevent="submit">
      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
      <SegmentedControl :model-value="scope" label="Applies to" mode="radio" :options="scopeOptions" @update:model-value="setScope($event)" />
      <p class="-mt-2 text-small text-fg-3">{{ ROUTING_SCOPE_HINT[scope] }}</p>
      <OwnerPicker v-if="scope !== 'Global'" id="reassign-owner" v-model="scopeIds" :label="ROUTING_SCOPE_PLURAL[scope]" :options="owners.options.value" :error="ownerError" />
      <CheckField v-model="enable" label="Enable the rule" description="Start applying it right away." />
    </form>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton variant="primary" type="submit" form="reassign-form" :loading="busy">Assign</UiButton>
    </template>
  </UiDialog>
</template>
