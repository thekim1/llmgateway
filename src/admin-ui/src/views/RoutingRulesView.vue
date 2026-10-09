<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { RoutingRule, RoutingScope } from '@/api/types'
import { ROUTING_SCOPES } from '@/api/types'
import ReassignRuleDialog from '@/components/rules/ReassignRuleDialog.vue'
import RuleDetail from '@/components/rules/RuleDetail.vue'
import RuleFormDrawer from '@/components/rules/RuleFormDrawer.vue'
import RuleList from '@/components/rules/RuleList.vue'
import RuleTestDialog from '@/components/rules/RuleTestDialog.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useUiStore } from '@/stores/ui'
import { ROUTING_SCOPE_LABEL } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import { groupRules } from '@/utils/routingRules'

type Filter = 'all' | 'orphaned' | RoutingScope

const rules = useRoutingRulesStore()
const ui = useUiStore()

const filter = ref<Filter>('all')
const selectedId = ref<string | null>(null)
const detail = ref<InstanceType<typeof RuleDetail> | null>(null)
const drawer = ref(false)
const editing = ref<RoutingRule | null>(null)
const deleting = ref(false)
const reassigning = ref(false)
const testing = ref(false)
const busy = ref(false)
const actionError = ref<unknown>(null)
const live = ref('')

const orphaned = computed(() => rules.items.filter((r) => r.isOrphaned))
const filtered = computed(() => {
  if (filter.value === 'all') return rules.items
  if (filter.value === 'orphaned') return orphaned.value
  return rules.items.filter((r) => r.scope === filter.value)
})
const sections = computed(() => groupRules(filtered.value))
const selected = computed(() => rules.items.find((r) => r.id === selectedId.value) ?? null)
const filterOptions = computed(() => [
  { value: 'all' as Filter, label: 'All', count: rules.items.length },
  ...ROUTING_SCOPES.map((s) => ({ value: s as Filter, label: ROUTING_SCOPE_LABEL[s], count: rules.items.filter((r) => r.scope === s).length })),
  ...(orphaned.value.length > 0 ? [{ value: 'orphaned' as Filter, label: 'Needs owner', count: orphaned.value.length }] : []),
])

/** The selected rule's neighbours: the rules of the same scope and owner, in checking order. */
const siblings = computed(() => {
  const rule = selected.value
  if (!rule) return []
  for (const section of groupRules(rules.items)) for (const group of section.groups) if (group.rules.some((r) => r.id === rule.id)) return group.rules
  return []
})
const position = computed(() => siblings.value.findIndex((r) => r.id === selectedId.value) + 1)

watch(
  [() => rules.items, filter],
  () => {
    if (filtered.value.some((r) => r.id === selectedId.value)) return
    selectedId.value = sections.value[0]?.groups[0]?.rules[0]?.id ?? null
  },
)

async function load(): Promise<void> {
  await rules.load()
}

function select(id: string): void {
  selectedId.value = id
  actionError.value = null
  void nextTick(() => detail.value?.focus())
}

function create(): void {
  editing.value = null
  drawer.value = true
}
function edit(): void {
  editing.value = selected.value
  drawer.value = true
}

async function guarded(action: () => Promise<void>): Promise<void> {
  busy.value = true
  actionError.value = null
  try {
    await action()
  } catch (e) {
    actionError.value = e
  } finally {
    busy.value = false
  }
}

const toggle = () =>
  guarded(async () => {
    const rule = selected.value
    if (!rule) return
    await rules.setEnabled(rule, !rule.isEnabled)
    ui.notify(rule.isEnabled ? 'Rule disabled' : 'Rule enabled')
  })

const move = (delta: -1 | 1) =>
  guarded(async () => {
    const rule = selected.value
    const at = position.value - 1
    const target = at + delta
    if (!rule || target < 0 || target >= siblings.value.length) return
    const ids = siblings.value.map((r) => r.id)
    ;[ids[at], ids[target]] = [ids[target]!, ids[at]!]
    await rules.reorder(rule.scope, rule.scopeId, ids)
    live.value = `${rule.name} is now checked ${target + 1} of ${ids.length}.`
  })

async function remove(): Promise<void> {
  if (!selected.value) return
  await rules.remove(selected.value.id)
  ui.notify('Rule deleted')
}

function saved(rule: RoutingRule): void {
  filter.value = 'all'
  select(rule.id)
}

onMounted(async () => {
  await load()
  if (!selectedId.value) selectedId.value = sections.value[0]?.groups[0]?.rules[0]?.id ?? null
})
</script>

<template>
  <PageHeader title="Routing rules">
    <template #description>A rule sends matching requests to a different model. The first match wins, most specific scope first.</template>
    <UiButton icon="play_arrow" @click="testing = true">Test a request</UiButton>
    <UiButton variant="primary" icon="add" @click="create">New rule</UiButton>
  </PageHeader>

  <p class="sr-only" role="status">{{ live }}</p>

  <AsyncState
    :loading="rules.loading"
    :error="rules.error"
    :empty="rules.items.length === 0"
    empty-text="No routing rules yet. Without rules every request goes to the model it asked for."
    @retry="load"
  >
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <SegmentedControl v-model="filter" :options="filterOptions" label="Filter rules by scope" />
        <p v-if="orphaned.length > 0 && filter !== 'orphaned'" class="flex flex-wrap items-center gap-2 text-small text-warn">
          {{ orphaned.length }} {{ orphaned.length === 1 ? 'rule needs' : 'rules need' }} a new owner and {{ orphaned.length === 1 ? 'is' : 'are' }} switched off.
          <UiButton size="sm" @click="filter = 'orphaned'">Show</UiButton>
        </p>
      </div>

      <div class="grid grid-cols-[minmax(260px,320px)_1fr] items-start gap-6 max-lg:grid-cols-1">
        <RuleList :sections="sections" :selected-id="selectedId" @select="select" />

        <div v-if="selected" class="flex min-w-0 flex-col gap-4">
          <InlineError v-if="actionError" :message="problemMessage(actionError)" :lang="problemLang(actionError)" />
          <RuleDetail
            ref="detail"
            :rule="selected"
            :position="position"
            :count="siblings.length"
            :busy="busy"
            @edit="edit"
            @toggle="toggle"
            @delete="deleting = true"
            @reassign="reassigning = true"
            @move="move"
          />
        </div>
      </div>
    </div>
  </AsyncState>

  <RuleFormDrawer v-model:open="drawer" :rule="editing" @saved="saved" />
  <ReassignRuleDialog v-model:open="reassigning" :rule="selected" />
  <RuleTestDialog v-model:open="testing" />
  <ConfirmDialog v-model:open="deleting" title="Delete rule?" confirm-label="Delete rule" danger :action="remove">
    <span class="font-medium">{{ selected?.name }}</span> is removed for good. Requests it matched go to the model they asked for, or to the next rule that matches.
  </ConfirmDialog>
</template>
