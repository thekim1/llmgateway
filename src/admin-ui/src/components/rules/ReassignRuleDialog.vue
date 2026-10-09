<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RoutingRule, RoutingScope } from '@/api/types'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'
import { ROUTING_SCOPE_HINT, ROUTING_SCOPE_LABEL } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import CheckField from '../ui/CheckField.vue'
import InlineError from '../ui/InlineError.vue'
import SegmentedControl from '../ui/SegmentedControl.vue'
import SelectField from '../ui/SelectField.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'

const props = defineProps<{ open: boolean; rule: RoutingRule | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const rules = useRoutingRulesStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const ui = useUiStore()

const scope = ref<RoutingScope>('Team')
const scopeId = ref('')
const enable = ref(true)
const busy = ref(false)
const error = ref<unknown>(null)
const ownerError = ref<string | null>(null)

const SCOPE_ORDER: RoutingScope[] = ['Global', 'Department', 'Team', 'VirtualKey']
const scopeOptions = SCOPE_ORDER.map((s) => ({ value: s, label: ROUTING_SCOPE_LABEL[s] }))
const ownerOptions = computed(() =>
  scope.value === 'Department'
    ? departments.items.map((d) => ({ value: d.id, label: d.name }))
    : scope.value === 'Team'
      ? teams.items.map((t) => ({ value: t.id, label: `${t.name} · ${t.departmentName}` }))
      : keys.items.map((k) => ({ value: k.id, label: `${k.name} · ${k.prefix}` })),
)

async function loadOwners(next: RoutingScope): Promise<void> {
  if (next === 'Department') await departments.load()
  else if (next === 'Team') await teams.load()
  else if (next === 'VirtualKey') await keys.load()
}

function setScope(next: RoutingScope): void {
  scope.value = next
  scopeId.value = ''
  ownerError.value = null
  void loadOwners(next)
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    error.value = null
    ownerError.value = null
    scope.value = props.rule && props.rule.scope !== 'Global' ? props.rule.scope : 'Team'
    scopeId.value = ''
    enable.value = true
    void loadOwners(scope.value)
  },
)

async function submit(): Promise<void> {
  if (!props.rule || busy.value) return
  error.value = null
  ownerError.value = null
  if (scope.value !== 'Global' && !scopeId.value) {
    ownerError.value = `Choose the ${ROUTING_SCOPE_LABEL[scope.value].toLowerCase()} this rule should apply to.`
    document.getElementById('reassign-owner')?.focus()
    return
  }
  busy.value = true
  try {
    await rules.reassign(props.rule.id, { scope: scope.value, scopeId: scope.value === 'Global' ? null : scopeId.value, enable: enable.value })
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
  <UiDialog :open="open" :title="rule?.isOrphaned ? 'Assign a new owner' : 'Change owner'" icon="account_tree" @update:open="emit('update:open', $event)">
    <template #description>
      <span class="font-medium">{{ rule?.name }}</span> keeps its condition, targets and order. Only who it applies to changes.
    </template>
    <form id="reassign-form" class="flex flex-col gap-4" novalidate @submit.prevent="submit">
      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
      <SegmentedControl :model-value="scope" label="Applies to" mode="radio" :options="scopeOptions" @update:model-value="setScope($event)" />
      <p class="-mt-2 text-small text-fg-3">{{ ROUTING_SCOPE_HINT[scope] }}</p>
      <SelectField
        v-if="scope !== 'Global'"
        id="reassign-owner"
        v-model="scopeId"
        :label="ROUTING_SCOPE_LABEL[scope]"
        :options="ownerOptions"
        :placeholder="`Choose a ${ROUTING_SCOPE_LABEL[scope].toLowerCase()}`"
        :error="ownerError"
      />
      <CheckField v-model="enable" label="Enable the rule" description="Start applying it right away." />
    </form>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton variant="primary" type="submit" form="reassign-form" :loading="busy">Assign</UiButton>
    </template>
  </UiDialog>
</template>
