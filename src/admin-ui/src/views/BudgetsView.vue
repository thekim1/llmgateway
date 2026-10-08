<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { BUDGET_PERIODS, BUDGET_SCOPES, type Budget, type BudgetPeriod, type BudgetScope } from '@/api/types'
import { useBudgetsStore } from '@/stores/budgets'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'
import { useFormErrors } from '@/composables/useFormErrors'
import { PERIOD_LABEL, SCOPE_LABEL } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import { formatDate, formatPercent, formatSek } from '@/utils/format'
import { parseDecimal } from '@/utils/validation'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import UiMeter from '@/components/ui/UiMeter.vue'

type ScopeFilter = 'all' | BudgetScope

const budgets = useBudgetsStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const ui = useUiStore()

const filter = ref<ScopeFilter>('all')
const ackError = ref<unknown>(null)
const ackBusy = ref<string | null>(null)

const drawerOpen = ref(false)
const editing = ref<Budget | null>(null)
const saving = ref(false)
const deleteOpen = ref(false)
const optionsError = ref<unknown>(null)
const form = reactive({
  scope: 'Team' as BudgetScope,
  scopeId: '',
  limit: '',
  period: 'Monthly' as BudgetPeriod,
  thresholds: '50, 80, 100',
  isActive: true,
})
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  scopeId: 'budget-scope-id',
  limitSek: 'budget-limit',
  alertThresholds: 'budget-thresholds',
})

const openAlerts = computed(() => budgets.alerts.filter((a) => !a.acknowledged))
const filtered = computed(() => (filter.value === 'all' ? budgets.items : budgets.items.filter((b) => b.scope === filter.value)))
const filterOptions = computed(() => [
  { value: 'all' as ScopeFilter, label: 'All', count: budgets.items.length },
  ...BUDGET_SCOPES.map((s) => ({ value: s as ScopeFilter, label: SCOPE_LABEL[s], count: budgets.items.filter((b) => b.scope === s).length })),
])
const scopeOptions = BUDGET_SCOPES.map((s) => ({ value: s, label: SCOPE_LABEL[s] }))
const periodOptions = BUDGET_PERIODS.map((p) => ({ value: p, label: PERIOD_LABEL[p] }))
const ownerOptions = computed(() => {
  if (form.scope === 'Department') return departments.items.map((d) => ({ value: d.id, label: d.name }))
  if (form.scope === 'Team') return teams.items.map((t) => ({ value: t.id, label: `${t.name} · ${t.departmentName}` }))
  return keys.items.map((k) => ({ value: k.id, label: `${k.name} · ${k.prefix}` }))
})
const ownerLabel = computed(() => SCOPE_LABEL[form.scope])

const columns: Column[] = [
  { key: 'owner', label: 'Owner' },
  { key: 'period', label: 'Period' },
  { key: 'usage', label: 'Used', class: 'min-w-48' },
  { key: 'amount', label: 'Spent / limit', align: 'right' },
  { key: 'resets', label: 'Resets', align: 'right' },
]

const load = () => Promise.all([budgets.load(), budgets.loadAlerts(false)])
onMounted(load)

function focusAfterAck(nextId: string | undefined): void {
  void nextTick(() => {
    const target = nextId ? document.getElementById(`ack-${nextId}`) : document.getElementById('open-alerts-h')
    target?.focus()
  })
}

async function acknowledge(id: string): Promise<void> {
  const list = openAlerts.value
  const index = list.findIndex((a) => a.id === id)
  const next = list[index + 1] ?? list[index - 1]
  ackBusy.value = id
  ackError.value = null
  try {
    await budgets.acknowledge(id)
    ui.notify('Alert acknowledged')
    focusAfterAck(next?.id)
  } catch (e) {
    ackError.value = e
  } finally {
    ackBusy.value = null
  }
}

async function loadOwners(scope: BudgetScope): Promise<void> {
  optionsError.value = null
  try {
    if (scope === 'Department') await departments.load()
    else if (scope === 'Team') await teams.load()
    else await keys.load()
  } catch (e) {
    optionsError.value = e
  }
  if (scope === 'Department' && departments.error) optionsError.value = departments.error
  if (scope === 'Team' && teams.error) optionsError.value = teams.error
  if (scope === 'VirtualKey' && keys.error) optionsError.value = keys.error
}

function setScope(scope: BudgetScope): void {
  form.scope = scope
  form.scopeId = ''
  void loadOwners(scope)
}

async function openForm(budget: Budget | null): Promise<void> {
  clear()
  editing.value = budget
  form.scope = budget?.scope ?? 'Team'
  form.scopeId = budget?.scopeId ?? ''
  form.limit = budget ? String(budget.limitSek).replace('.', ',') : ''
  form.period = budget?.period ?? 'Monthly'
  form.thresholds = budget ? budget.alertThresholds.join(', ') : '50, 80, 100'
  form.isActive = budget?.isActive ?? true
  drawerOpen.value = true
  await loadOwners(form.scope)
}

function parseThresholds(): number[] | null {
  const parts = form.thresholds.split(/[\s,;]+/).filter(Boolean)
  if (parts.length === 0) return null
  const values = parts.map((p) => parseDecimal(p))
  return values.every((v): v is number => v !== null && Number.isFinite(v) && v > 0 && v <= 100) ? values : null
}

function focusInvalid(): void {
  void nextTick(() => document.querySelector<HTMLElement>('[role=dialog] [aria-invalid=true]')?.focus())
}

async function save(): Promise<void> {
  const limit = parseDecimal(form.limit)
  const thresholds = parseThresholds()
  const ok = validate({
    scopeId: !form.scopeId && `Choose the ${ownerLabel.value.toLowerCase()} this budget applies to.`,
    limitSek: (limit === null || Number.isNaN(limit) || limit <= 0) && 'Enter a limit above 0 kr, for example 5000.',
    alertThresholds: !thresholds && 'Enter alert levels as percentages from 1 to 100, separated by commas, for example 50, 80, 100.',
  })
  if (!ok || limit === null || !thresholds) {
    focusInvalid()
    return
  }
  saving.value = true
  const request = { scope: form.scope, scopeId: form.scopeId, limitSek: limit, period: form.period, alertThresholds: [...new Set(thresholds)].sort((a, b) => a - b), isActive: form.isActive }
  try {
    if (editing.value) await budgets.update(editing.value.id, request)
    else await budgets.create(request)
    ui.notify(editing.value ? 'Budget saved' : 'Budget created')
    drawerOpen.value = false
    void budgets.load()
  } catch (e) {
    applyServerError(e)
    focusInvalid()
  } finally {
    saving.value = false
  }
}

async function remove(): Promise<void> {
  if (!editing.value) return
  await budgets.remove(editing.value.id)
  ui.notify('Budget deleted')
  drawerOpen.value = false
}
</script>

<template>
  <PageHeader title="Budgets & alerts" description="Spend limits per department, team and key, with alerts when they are close.">
    <UiButton variant="primary" icon="add" @click="openForm(null)">New budget</UiButton>
  </PageHeader>

  <section aria-labelledby="open-alerts-h" class="mb-8">
    <h2 id="open-alerts-h" tabindex="-1" class="mb-3 text-heading outline-none">Open alerts</h2>
    <AsyncState :loading="budgets.alertsLoading && openAlerts.length === 0" :error="budgets.alertsError" :empty="false" @retry="load">
      <InlineError v-if="ackError" :message="problemMessage(ackError)" :lang="problemLang(ackError)" class="mb-3" />
      <p v-if="openAlerts.length === 0" class="flex items-center gap-2 text-fg-2"><AppIcon name="check_circle" filled class="text-ok" />No open alerts.</p>
      <ul v-else class="m-0 flex list-none flex-col gap-2 p-0">
        <li v-for="alert in openAlerts" :key="alert.id" class="flex flex-wrap items-center justify-between gap-3 rounded-card bg-warn-soft p-4">
          <div class="flex min-w-0 items-start gap-3">
            <AppIcon name="warning" filled class="mt-0.5 text-warn" />
            <div class="min-w-0">
              <p class="font-medium text-fg"><span lang="sv">{{ alert.scopeName }}</span> passed {{ alert.thresholdPercent }}%</p>
              <p class="text-small text-fg-2">{{ SCOPE_LABEL[alert.scope] }} budget · {{ formatSek(alert.spentSek) }} of {{ formatSek(alert.limitSek) }}</p>
            </div>
          </div>
          <UiButton :id="`ack-${alert.id}`" size="sm" :loading="ackBusy === alert.id" :aria-label="`Acknowledge alert for ${alert.scopeName}`" @click="acknowledge(alert.id)">Acknowledge</UiButton>
        </li>
      </ul>
    </AsyncState>
  </section>

  <section aria-labelledby="budgets-h">
    <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
      <h2 id="budgets-h" class="text-heading">Budgets</h2>
      <SegmentedControl v-model="filter" :options="filterOptions" label="Filter by scope" />
    </div>
    <AsyncState :loading="budgets.loading && !budgets.loaded" :error="budgets.error" :empty="false" @retry="load">
      <DataTable
        :columns="columns"
        :rows="filtered"
        :row-key="(r) => r.id"
        caption="Budgets"
        clickable
        clearable
        empty-text="No budgets match this filter."
        @row-click="openForm"
        @clear="filter = 'all'"
      >
        <template #cell-owner="{ row }">
          <span class="font-medium" lang="sv">{{ row.scopeName }}</span>
          <span class="block text-caption text-fg-3">{{ SCOPE_LABEL[row.scope] }}<template v-if="!row.isActive"> · Inactive</template></span>
        </template>
        <template #cell-period="{ row }">{{ PERIOD_LABEL[row.period] }}</template>
        <template #cell-usage="{ row }">
          <span class="flex items-center gap-2">
            <UiMeter :value="row.percentUsed" :label="`${row.scopeName} budget used`" :ticks="row.alertThresholds" class="flex-1" />
            <span class="tabular w-12 text-right text-small font-medium">{{ formatPercent(row.percentUsed) }}</span>
          </span>
        </template>
        <template #cell-amount="{ row }">{{ formatSek(row.spentSek) }} <span class="text-fg-3">/ {{ formatSek(row.limitSek) }}</span></template>
        <template #cell-resets="{ row }">{{ formatDate(row.periodEnd) }}</template>
      </DataTable>
    </AsyncState>
    <p class="mt-3 text-small text-fg-3">Amounts are estimates from recorded usage, not invoices. Ticks on the meters mark the alert levels.</p>
  </section>

  <UiDrawer v-model:open="drawerOpen" wide :title="editing ? 'Edit budget' : 'New budget'" :subtitle="editing?.scopeName" description="Set the owner, limit, period and alert levels for a budget.">
    <template v-if="editing" #badge>
      <UiBadge :tone="editing.isActive ? 'ok' : 'neutral'" :label="editing.isActive ? 'Active' : 'Inactive'" />
    </template>
    <form id="budget-form" class="flex flex-col gap-5" novalidate @submit.prevent="save">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <SegmentedControl :model-value="form.scope" :options="scopeOptions" label="Applies to" mode="radio" @update:model-value="setScope" />
      <InlineError v-if="optionsError" :message="problemMessage(optionsError)" :lang="problemLang(optionsError)" />
      <SelectField v-model="form.scopeId" :label="ownerLabel" :options="ownerOptions" placeholder="Choose…" :error="errors.scopeId" />
      <TextField v-model="form.limit" label="Limit (kr)" inputmode="decimal" :error="errors.limitSek" hint="Total spend allowed per period." />
      <SelectField v-model="form.period" label="Period" :options="periodOptions" />
      <TextField v-model="form.thresholds" label="Alert levels (%)" :error="errors.alertThresholds" hint="An alert opens when spend passes each level, for example 50, 80, 100." />
      <CheckField v-model="form.isActive" label="Active" description="Inactive budgets are kept but not enforced." />
    </form>
    <template #footer>
      <UiButton type="submit" form="budget-form" variant="primary" :loading="saving">{{ editing ? 'Save budget' : 'Create budget' }}</UiButton>
      <UiButton v-if="editing" variant="danger" class="ml-auto" @click="deleteOpen = true">Delete</UiButton>
    </template>
  </UiDrawer>

  <ConfirmDialog
    v-model:open="deleteOpen"
    title="Delete this budget?"
    consequence="Spend is no longer limited and its open alerts stop. Recorded usage is kept."
    confirm-label="Delete budget"
    danger
    :action="remove"
  />
</template>
