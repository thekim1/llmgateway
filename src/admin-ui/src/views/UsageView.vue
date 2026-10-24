<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { USAGE_GROUP_BY, type UsageGroupBy, type UsageRequest } from '@/api/types'
import { useUsageStore } from '@/stores/usage'
import { useDepartmentsStore } from '@/stores/departments'
import { useTeamsStore } from '@/stores/teams'
import { useKeysStore } from '@/stores/keys'
import { OUTCOME_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { formatDate, formatDateTime, formatMs, formatNumber, formatSek, formatUsdCost, todayIso, toIsoDate } from '@/utils/format'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import DetailSection from '@/components/ui/DetailSection.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import UsageDashboard from '@/components/usage/UsageDashboard.vue'

const PAGE_SIZE = 50
const GROUP_LABEL: Record<UsageGroupBy, string> = {
  department: 'Department',
  team: 'Team',
  key: 'Key',
  model: 'Model',
  provider: 'Provider',
  day: 'Day',
}

type Tab = 'dashboard' | 'requests'
const tabs: { value: Tab; label: string; icon: string }[] = [
  { value: 'dashboard', label: 'Dashboard', icon: 'monitoring' },
  { value: 'requests', label: 'Requests and costs', icon: 'receipt_long' },
]

const route = useRoute()
const router = useRouter()
// Other pages (PII incidents) link to a request as /usage?request=<id>, which lives on the requests tab.
const tab = ref<Tab>(route.query.tab === 'requests' || typeof route.query.request === 'string' ? 'requests' : 'dashboard')
let requestsLoaded = false
const usage = useUsageStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const departmentId = ref('')
const teamId = ref('')
const keyId = ref('')
const from = ref(toIsoDate(new Date(Date.now() - 30 * 86_400_000)))
const to = ref(todayIso())
const group = ref<UsageGroupBy>('department')
const page = ref(1)
const rangeError = ref<string | null>(null)

const lookupId = ref('')
const lookupBusy = ref(false)
const lookupError = ref<unknown>(null)
const lookupMessage = ref<string | null>(null)
const selected = ref<UsageRequest | null>(null)
const drawerOpen = ref(false)

const dates = computed(() => ({
  from: new Date(`${from.value}T00:00:00`).toISOString(),
  to: new Date(`${to.value}T23:59:59.999`).toISOString(),
}))
const groupOptions = USAGE_GROUP_BY.map((value) => ({ value, label: GROUP_LABEL[value] }))
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
function onDepartmentChange(): void {
  if (teamId.value && !teams.items.some((t) => t.id === teamId.value && t.departmentId === departmentId.value)) teamId.value = ''
  onTeamChange()
}
function onTeamChange(): void {
  if (keyId.value && !keys.items.some((k) => k.id === keyId.value && (!teamId.value || k.teamId === teamId.value) && (!departmentId.value || k.departmentId === departmentId.value))) keyId.value = ''
}
const scope = computed(() => ({
  ...(departmentId.value && { departmentId: departmentId.value }),
  ...(teamId.value && { teamId: teamId.value }),
}))
const requestScope = computed(() => ({ ...scope.value, ...(keyId.value && { keyId: keyId.value }) }))
const exportUrl = computed(() => (from.value && to.value ? usage.exportCsvUrl(dates.value.from, dates.value.to) : undefined))
const pageCount = computed(() => Math.max(1, Math.ceil(usage.requests.total / PAGE_SIZE)))

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
  await Promise.all([
    usage.loadSummary({ ...dates.value, ...scope.value, groupBy: group.value }),
    usage.loadRequests({ ...dates.value, ...requestScope.value, page: page.value, pageSize: PAGE_SIZE }),
  ])
}

function apply(): void {
  page.value = 1
  void load()
}

function goTo(next: number): void {
  page.value = next
  void usage.loadRequests({ ...dates.value, ...requestScope.value, page: next, pageSize: PAGE_SIZE })
}

function openRequest(request: UsageRequest): void {
  selected.value = request
  drawerOpen.value = true
}

async function lookup(): Promise<void> {
  const id = lookupId.value.trim()
  lookupError.value = null
  lookupMessage.value = null
  if (!id) {
    lookupMessage.value = 'Enter a request ID to look it up.'
    return
  }
  lookupBusy.value = true
  try {
    openRequest(await usage.lookup(id))
  } catch (e) {
    lookupError.value = e
  } finally {
    lookupBusy.value = false
  }
}

const summaryColumns: Column[] = [
  { key: 'label', label: 'Name' },
  { key: 'requests', label: 'Requests', align: 'right' },
  { key: 'tokens', label: 'Tokens', align: 'right' },
  { key: 'costSek', label: 'Cost', align: 'right' },
  { key: 'errors', label: 'Errors', align: 'right' },
]
const requestColumns: Column[] = [
  { key: 'requestId', label: 'Request' },
  { key: 'timestamp', label: 'Time' },
  { key: 'keyName', label: 'Key' },
  { key: 'costSek', label: 'Cost', align: 'right' },
  { key: 'outcome', label: 'Outcome' },
]

function openRequestsTab(): void {
  if (requestsLoaded) return
  requestsLoaded = true
  void load()
}

// Search links to /usage?tab=requests; follow it when already on the page.
watch(() => route.query.tab, (value) => {
  const next: Tab = value === 'requests' ? 'requests' : 'dashboard'
  if (next !== tab.value) tab.value = next
})

watch(tab, (value) => {
  void router.replace({ query: { ...route.query, tab: value === 'dashboard' ? undefined : value } })
  if (value === 'requests') openRequestsTab()
})

onMounted(() => {
  void departments.load()
  void teams.load()
  void keys.load()
  if (tab.value === 'requests') openRequestsTab()
  if (typeof route.query.request === 'string' && route.query.request) {
    lookupId.value = route.query.request
    void lookup()
  }
})
</script>

<template>
  <PageHeader title="Usage" description="How the gateway is used and how it performs: requests, cost, tokens and timings. Metadata only, never prompt or response content.">
    <UiButton :href="exportUrl" icon="download" :disabled="!exportUrl" download>Export CSV</UiButton>
  </PageHeader>

  <UiTabs id="usage" v-model="tab" :tabs="tabs" label="Usage views" class="mb-6" />

  <div v-if="tab === 'dashboard'" id="usage-panel-dashboard" role="tabpanel" aria-labelledby="usage-tab-dashboard">
    <UsageDashboard />
  </div>

  <div v-else id="usage-panel-requests" role="tabpanel" aria-labelledby="usage-tab-requests">

    <form class="mb-6 flex flex-wrap items-end gap-3" novalidate @submit.prevent="apply">
      <TextField v-model="from" label="From" type="date" />
      <TextField v-model="to" label="To" type="date" />
      <SelectField v-model="departmentId" label="Department" :options="departmentOptions" @update:model-value="onDepartmentChange" />
      <SelectField v-model="teamId" label="Team" :options="teamOptions" @update:model-value="onTeamChange" />
      <SelectField v-model="keyId" label="Key" :options="keyOptions" />
      <SelectField v-model="group" label="Group by" :options="groupOptions" />
      <UiButton type="submit" variant="primary">Apply</UiButton>
    </form>
    <InlineError v-if="rangeError" :message="rangeError" class="mb-6" />

    <section aria-labelledby="summary-h" class="mb-8">
      <h2 id="summary-h" class="mb-3 text-heading">Summary by {{ GROUP_LABEL[group].toLowerCase() }}</h2>
      <AsyncState :loading="usage.summaryLoading" :error="usage.summaryError" :empty="false" @retry="load">
        <p v-if="usage.summary" class="mb-3 text-fg-2">
          {{ formatSek(usage.summary.totalCostSek) }} across {{ formatNumber(usage.summary.totalRequests) }} requests and
          {{ formatNumber(usage.summary.totalInputTokens + usage.summary.totalOutputTokens) }} tokens.
        </p>
        <p v-if="usage.summary?.monthlyTotalsBefore" class="mb-3 flex items-start gap-2 text-small text-fg-2">
          <AppIcon name="info" :size="18" class="mt-px shrink-0" />
          Usage before {{ formatDate(usage.summary.monthlyTotalsBefore) }} is only kept as monthly totals (data retention, see Settings). Those months count in full, and by day they show as one row per month.
        </p>
        <DataTable :columns="summaryColumns" :rows="usage.summary?.rows ?? []" :row-key="(r) => r.key" caption="Usage summary" empty-text="No usage in this period.">
          <template #cell-label="{ row }"><span class="font-medium">{{ row.label }}</span></template>
          <template #cell-requests="{ row }">{{ formatNumber(row.requests) }}</template>
          <template #cell-tokens="{ row }">{{ formatNumber(row.inputTokens + row.outputTokens) }}</template>
          <template #cell-costSek="{ row }">{{ formatSek(row.costSek) }}</template>
          <template #cell-errors="{ row }">{{ formatNumber(row.errors) }}</template>
        </DataTable>
      </AsyncState>
    </section>

    <section aria-labelledby="requests-h" class="mb-8">
      <h2 id="requests-h" class="mb-3 text-heading">Requests</h2>
      <AsyncState :loading="usage.requestsLoading" :error="usage.requestsError" :empty="false" @retry="load">
        <DataTable
          :columns="requestColumns"
          :rows="usage.requests.items"
          :row-key="(r) => r.requestId"
          caption="Requests in this period"
          empty-text="No requests in this period."
          clickable
          @row-click="openRequest"
        >
          <template #cell-requestId="{ row }"><span class="font-mono text-small">{{ row.requestId }}</span></template>
          <template #cell-timestamp="{ row }">{{ formatDateTime(row.timestamp) }}</template>
          <template #cell-keyName="{ row }">
            <span class="font-medium">{{ row.keyName }}</span>
            <span class="block text-caption text-fg-3"><span lang="sv">{{ row.teamName }}</span> · <span lang="sv">{{ row.departmentName }}</span></span>
          </template>
          <template #cell-costSek="{ row }">{{ formatSek(row.costSek) }}</template>
          <template #cell-outcome="{ row }">
            <UiBadge :tone="OUTCOME_LABEL[row.outcome].tone" :label="OUTCOME_LABEL[row.outcome].label" />
            <span class="ml-2 text-caption text-fg-3">{{ row.statusCode }}</span>
          </template>
        </DataTable>
        <nav v-if="usage.requests.total > PAGE_SIZE" aria-label="Request pages" class="mt-3 flex items-center justify-between gap-3">
          <span class="text-small text-fg-2">Page {{ page }} of {{ pageCount }}</span>
          <span class="flex gap-2">
            <UiButton size="sm" :disabled="page <= 1" @click="goTo(page - 1)">Previous</UiButton>
            <UiButton size="sm" :disabled="page >= pageCount" @click="goTo(page + 1)">Next</UiButton>
          </span>
        </nav>
      </AsyncState>
    </section>

    <UiCard title="Look up a request" heading-id="lookup-h">
      <form class="flex flex-wrap items-end gap-3" novalidate @submit.prevent="lookup">
        <TextField v-model="lookupId" label="Request ID" mono hint="Find it in the x-request-id response header." :error="lookupMessage" />
        <UiButton type="submit" :loading="lookupBusy">Look up</UiButton>
      </form>
      <InlineError v-if="lookupError" :message="problemMessage(lookupError)" class="mt-3" />
    </UiCard>
  </div>

  <UiDrawer v-model:open="drawerOpen" :title="selected?.keyName ?? 'Request'" :subtitle="selected?.requestId">
    <template v-if="selected">
      <DetailSection title="Request">
        <DetailRow label="Time">{{ formatDateTime(selected.timestamp, true) }}</DetailRow>
        <DetailRow label="Outcome">
          <UiBadge :tone="OUTCOME_LABEL[selected.outcome].tone" :label="OUTCOME_LABEL[selected.outcome].label" />
          <span class="ml-2 text-fg-3">{{ selected.statusCode }}</span>
        </DetailRow>
        <DetailRow label="Endpoint"><span class="font-mono">{{ selected.endpoint }}</span></DetailRow>
        <DetailRow label="Streamed">{{ selected.streamed ? 'Yes' : 'No' }}</DetailRow>
        <DetailRow v-if="selected.errorCode" label="Error code"><span class="font-mono">{{ selected.errorCode }}</span></DetailRow>
        <DetailRow v-if="selected.piiCategories" label="PII found">
          <span class="font-mono">{{ selected.piiCategories }}</span>
          <span v-if="selected.piiActionApplied" class="ml-2 text-fg-3">{{ selected.piiActionApplied }}</span>
        </DetailRow>
        <DetailRow v-if="selected.guardFlags" label="Content guard"><span class="font-mono">{{ selected.guardFlags }}</span></DetailRow>
      </DetailSection>
      <DetailSection title="Owner">
        <DetailRow label="Key">{{ selected.keyName }} <span class="font-mono text-fg-3">{{ selected.keyPrefix }}</span></DetailRow>
        <DetailRow label="Team"><span lang="sv">{{ selected.teamName }}</span></DetailRow>
        <DetailRow label="Department"><span lang="sv">{{ selected.departmentName }}</span></DetailRow>
      </DetailSection>
      <DetailSection title="Routing">
        <DetailRow label="Requested model"><span class="font-mono">{{ selected.requestedModel }}</span></DetailRow>
        <DetailRow v-if="selected.routingRuleName" label="Routing rule">{{ selected.routingRuleName }}</DetailRow>
        <DetailRow label="Provider">{{ selected.providerName ?? '—' }}</DetailRow>
        <DetailRow label="Upstream model"><span class="font-mono">{{ selected.upstreamModel ?? '—' }}</span></DetailRow>
        <DetailRow label="Fallbacks">{{ formatNumber(selected.fallbackCount) }}</DetailRow>
      </DetailSection>
      <DetailSection title="Cost and speed">
        <DetailRow label="Input tokens">{{ formatNumber(selected.inputTokens) }}</DetailRow>
        <DetailRow label="Cached input">{{ formatNumber(selected.cachedInputTokens) }}</DetailRow>
        <DetailRow label="Output tokens">{{ formatNumber(selected.outputTokens) }}</DetailRow>
        <DetailRow label="Cost">{{ formatUsdCost(selected.costUsd) }} <span class="text-fg-3">{{ formatSek(selected.costSek) }}</span></DetailRow>
        <DetailRow label="Latency">{{ formatMs(selected.latencyMs) }}</DetailRow>
        <DetailRow v-if="selected.upstreamMs !== null" label="Provider time">{{ formatMs(selected.upstreamMs) }}</DetailRow>
        <DetailRow v-if="selected.upstreamMs !== null" label="Gateway overhead">{{ formatMs(Math.max(0, selected.latencyMs - selected.upstreamMs)) }}</DetailRow>
        <DetailRow v-if="selected.timeToFirstTokenMs !== null" label="Time to first token">{{ formatMs(selected.timeToFirstTokenMs) }}</DetailRow>
      </DetailSection>
    </template>
  </UiDrawer>
</template>
