<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { PII_CATEGORIES, type PiiAction, type PiiCategory } from '@/api/types'
import { useUsageStore } from '@/stores/usage'
import { useDepartmentsStore } from '@/stores/departments'
import { useTeamsStore } from '@/stores/teams'
import { useKeysStore } from '@/stores/keys'
import { OUTCOME_LABEL, PII_ACTION_LABEL, PII_CATEGORY_LABEL } from '@/utils/labels'
import { formatDateTime, formatNumber, todayIso, toIsoDate } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import Pagination from '@/components/ui/Pagination.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

const PAGE_SIZE = 50
const ACTIONS = Object.keys(PII_ACTION_LABEL) as PiiAction[]

const usage = useUsageStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const departmentId = ref('')
const teamId = ref('')
const keyId = ref('')
const category = ref<PiiCategory | ''>('')
const action = ref<PiiAction | ''>('')
const from = ref(toIsoDate(new Date(Date.now() - 30 * 86_400_000)))
const to = ref(todayIso())
const page = ref(1)
const rangeError = ref<string | null>(null)

const departmentOptions = computed(() => [{ value: '', label: 'All departments' }, ...departments.items.map((d) => ({ value: d.id, label: d.name }))])
const teamOptions = computed(() => [
  { value: '', label: 'All teams' },
  ...teams.items.filter((t) => !departmentId.value || t.departmentId === departmentId.value).map((t) => ({ value: t.id, label: t.name })),
])
const keyOptions = computed(() => [
  { value: '', label: 'All keys' },
  ...keys.items
    .filter((k) => (!departmentId.value || k.departmentId === departmentId.value) && (!teamId.value || k.teamId === teamId.value))
    .map((k) => ({ value: k.id, label: k.name })),
])
const categoryOptions = [{ value: '', label: 'All categories' }, ...PII_CATEGORIES.map((c) => ({ value: c, label: PII_CATEGORY_LABEL[c] }))]
const actionOptions = [{ value: '', label: 'All actions' }, ...ACTIONS.map((a) => ({ value: a, label: PII_ACTION_LABEL[a].label }))]

function onDepartmentChange(): void {
  if (teamId.value && !teams.items.some((t) => t.id === teamId.value && t.departmentId === departmentId.value)) teamId.value = ''
  onTeamChange()
}
function onTeamChange(): void {
  if (keyId.value && !keys.items.some((k) => k.id === keyId.value && (!teamId.value || k.teamId === teamId.value) && (!departmentId.value || k.departmentId === departmentId.value))) keyId.value = ''
}

const report = computed(() => usage.piiIncidents)
const filtered = computed(() => category.value !== '' || action.value !== '')
const categoryTiles = computed(() =>
  PII_CATEGORIES.map((c) => {
    const row = report.value?.categories.find((x) => x.category === c)
    return { category: c, requests: row?.requests ?? 0, occurrences: row?.occurrences ?? 0 }
  }),
)
const actionTiles = computed(() => ACTIONS.map((a) => ({ action: a, requests: report.value?.actions.find((x) => x.action === a)?.requests ?? 0 })))

async function load(): Promise<void> {
  if (!from.value || !to.value) {
    rangeError.value = 'Choose both a start and an end date.'
    return
  }
  if (from.value > to.value) {
    rangeError.value = 'The start date must be on or before the end date.'
    return
  }
  rangeError.value = null
  await usage.loadPiiIncidents({
    from: new Date(`${from.value}T00:00:00`).toISOString(),
    to: new Date(`${to.value}T23:59:59.999`).toISOString(),
    ...(departmentId.value && { departmentId: departmentId.value }),
    ...(teamId.value && { teamId: teamId.value }),
    ...(keyId.value && { keyId: keyId.value }),
    ...(category.value && { category: category.value }),
    ...(action.value && { action: action.value }),
    page: page.value,
    pageSize: PAGE_SIZE,
  })
}

function apply(): void {
  page.value = 1
  void load()
}

function setPage(next: number): void {
  page.value = next
  void load()
}

function clearFilters(): void {
  category.value = ''
  action.value = ''
  apply()
}

const columns: Column[] = [
  { key: 'timestamp', label: 'Time' },
  { key: 'keyName', label: 'Key' },
  { key: 'categories', label: 'Found' },
  { key: 'action', label: 'Action' },
  { key: 'model', label: 'Model' },
  { key: 'outcome', label: 'Outcome' },
  { key: 'requestId', label: 'Request' },
]

onMounted(() => {
  void departments.load()
  void teams.load()
  void keys.load()
  void load()
})
</script>

<template>
  <PageHeader
    title="PII incidents"
    description="Requests in which the gateway found personal data, and what it did. Only the category and count are stored, never the values."
  />

  <form class="mb-6 flex flex-wrap items-end gap-3" novalidate @submit.prevent="apply">
    <TextField v-model="from" label="From" type="date" />
    <TextField v-model="to" label="To" type="date" />
    <SelectField v-model="departmentId" label="Department" :options="departmentOptions" @update:model-value="onDepartmentChange" />
    <SelectField v-model="teamId" label="Team" :options="teamOptions" @update:model-value="onTeamChange" />
    <SelectField v-model="keyId" label="Key" :options="keyOptions" />
    <SelectField v-model="category" label="Category" :options="categoryOptions" />
    <SelectField v-model="action" label="Action" :options="actionOptions" />
    <UiButton type="submit" variant="primary">Apply</UiButton>
  </form>
  <InlineError v-if="rangeError" :message="rangeError" class="mb-6" />

  <AsyncState :loading="usage.piiIncidentsLoading" :error="usage.piiIncidentsError" :empty="!report" @retry="load">
    <template v-if="report">
      <section aria-labelledby="pii-summary-h" class="mb-8">
        <h2 id="pii-summary-h" class="mb-3 text-heading">Summary</h2>
        <p class="mb-3 text-fg-2">
          {{ formatNumber(report.totalIncidents) }} {{ report.totalIncidents === 1 ? 'request' : 'requests' }} with PII in this period.
          Category and action filters apply to the list below, not to these figures.
        </p>
        <h3 class="mb-2 text-small font-medium text-fg-2">By category</h3>
        <div class="mb-4 grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(180px,1fr))]" data-testid="category-tiles">
          <MetricTile
            v-for="tile in categoryTiles"
            :key="tile.category"
            :label="PII_CATEGORY_LABEL[tile.category]"
            :value="formatNumber(tile.requests)"
            :context="`${formatNumber(tile.occurrences)} ${tile.occurrences === 1 ? 'value' : 'values'} found`"
          />
        </div>
        <h3 class="mb-2 text-small font-medium text-fg-2">By action</h3>
        <div class="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(180px,1fr))]" data-testid="action-tiles">
          <MetricTile v-for="tile in actionTiles" :key="tile.action" :label="PII_ACTION_LABEL[tile.action].label" :value="formatNumber(tile.requests)" />
        </div>
      </section>

      <section aria-labelledby="pii-list-h">
        <h2 id="pii-list-h" class="mb-3 text-heading">Incidents</h2>
        <p v-if="report.totalIncidents === 0" class="mb-3 rounded-card bg-surface-2 p-4 text-fg-2" data-testid="pii-explainer">
          PII scanning is set per key. A key with PII policy <strong>Off</strong> is not scanned, so its requests never show up here.
          Choose Allow (detect and record), Redact, Block or On-prem on the key to start recording incidents.
        </p>
        <DataTable
          :columns="columns"
          :rows="report.items"
          :row-key="(r) => r.requestId"
          caption="Requests with PII"
          :clearable="filtered"
          :empty-text="filtered ? 'No incidents match these filters.' : 'No PII found in this period.'"
          @clear="clearFilters"
        >
          <template #cell-timestamp="{ row }"><span class="tabular">{{ formatDateTime(row.timestamp, true) }}</span></template>
          <template #cell-keyName="{ row }">
            <span class="font-medium">{{ row.keyName }}</span> <span class="font-mono text-caption text-fg-3">{{ row.keyPrefix }}</span>
            <span class="block text-caption text-fg-3"><span lang="sv">{{ row.teamName }}</span> · <span lang="sv">{{ row.departmentName }}</span></span>
          </template>
          <template #cell-categories="{ row }">
            <ul class="flex flex-wrap gap-1">
              <li v-for="f in row.categories" :key="f.category">
                <UiBadge :dot="false">{{ PII_CATEGORY_LABEL[f.category] }} × {{ formatNumber(f.count) }}</UiBadge>
              </li>
            </ul>
          </template>
          <template #cell-action="{ row }"><UiBadge :tone="PII_ACTION_LABEL[row.action].tone" :label="PII_ACTION_LABEL[row.action].label" /></template>
          <template #cell-model="{ row }">
            <span class="font-mono text-small">{{ row.requestedModel }}</span>
            <span class="block text-caption text-fg-3">{{ row.providerName ?? '—' }} · <span class="font-mono">{{ row.endpoint }}</span></span>
          </template>
          <template #cell-outcome="{ row }">
            <UiBadge :tone="OUTCOME_LABEL[row.outcome].tone" :label="OUTCOME_LABEL[row.outcome].label" />
            <span class="ml-2 text-caption text-fg-3">{{ row.statusCode }}</span>
          </template>
          <template #cell-requestId="{ row }">
            <RouterLink :to="{ name: 'usage', query: { request: row.requestId } }" class="font-mono text-small text-accent-ink underline">
              {{ row.requestId }}
            </RouterLink>
          </template>
        </DataTable>
        <Pagination :page="page" :page-size="PAGE_SIZE" :total="report.total" label="Incident pages" @update:page="setPage" />
      </section>
    </template>
  </AsyncState>
</template>
