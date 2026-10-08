<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { UsageSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useBudgetsStore } from '@/stores/budgets'
import { useOpsStore } from '@/stores/ops'
import { CIRCUIT_LABEL, HEALTH_STATUS, SCOPE_LABEL, plural } from '@/utils/labels'
import { formatMs, formatNumber, formatPercent, formatSek, startOfMonthIso } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import AttentionCard from '@/components/ui/AttentionCard.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiCard from '@/components/ui/UiCard.vue'
import DailySpendChart from '@/components/overview/DailySpendChart.vue'

const router = useRouter()
const auth = useAuthStore()
const budgets = useBudgetsStore()
const ops = useOpsStore()

const DAY_MS = 86_400_000
const month = ref<UsageSummary | null>(null)
const daily = ref<UsageSummary | null>(null)
const last24h = ref<UsageSummary | null>(null)
const loading = ref(false)
const error = ref<unknown>(null)

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  const now = Date.now()
  try {
    const [m, d, h] = await Promise.all([
      api.usage.summary({ from: `${startOfMonthIso()}T00:00:00`, groupBy: 'department' }),
      api.usage.summary({ from: new Date(now - 29 * DAY_MS).toISOString(), groupBy: 'day' }),
      api.usage.summary({ from: new Date(now - DAY_MS).toISOString(), groupBy: 'day' }),
    ])
    month.value = m
    daily.value = d
    last24h.value = h
  } catch (e) {
    error.value = e
  } finally {
    loading.value = false
  }
  if (auth.canManage) {
    void budgets.load()
    void budgets.loadAlerts(false)
  }
  if (auth.isGatewayAdmin) void ops.loadHealth()
}
onMounted(load)

const openAlerts = computed(() => budgets.alerts.filter((a) => !a.acknowledged))
const openCircuits = computed(() => ops.health?.providers.filter((p) => p.circuitState === 'Open') ?? [])
const attentionCount = computed(() => openAlerts.value.length + openCircuits.value.length)

const budgetPercent = computed(() => {
  const active = budgets.items.filter((b) => b.isActive && b.scope === 'Department')
  const limit = active.reduce((sum, b) => sum + b.limitSek, 0)
  if (limit <= 0) return null
  return (active.reduce((sum, b) => sum + b.spentSek, 0) / limit) * 100
})

const errorRate = computed(() => {
  const total = last24h.value?.totalRequests ?? 0
  return total > 0 ? ((last24h.value?.totalErrors ?? 0) / total) * 100 : null
})
const p95 = computed(() => {
  const values = (ops.health?.providers ?? []).map((p) => p.p95LatencyMs).filter((v): v is number => v !== null)
  return values.length ? Math.max(...values) : null
})
const points = computed(() =>
  [...(daily.value?.rows ?? [])].sort((a, b) => a.key.localeCompare(b.key)).map((r) => ({ day: r.key, costSek: r.costSek })),
)

const columns: Column[] = [
  { key: 'label', label: 'Department' },
  { key: 'requests', label: 'Requests', align: 'right' },
  { key: 'costSek', label: 'Spend', align: 'right' },
]
const providerColumns: Column[] = [
  { key: 'name', label: 'Provider' },
  { key: 'status', label: 'Status' },
  { key: 'p95', label: 'p95', align: 'right' },
]
const providerRows = computed(() => ops.health?.providers.filter((p) => p.isEnabled) ?? [])
const providerStatus = (circuit: 'Open' | 'Closed', errorRate24h: number) =>
  circuit === 'Open' ? CIRCUIT_LABEL.Open : errorRate24h >= 0.5 ? HEALTH_STATUS.Unhealthy : errorRate24h >= 0.05 ? HEALTH_STATUS.Degraded : HEALTH_STATUS.Healthy
</script>

<template>
  <PageHeader title="Overview" description="Spend, traffic and anything that needs a decision." />

  <AsyncState :loading="loading && !month" :error="error" :empty="false" @retry="load">
    <section v-if="attentionCount > 0" aria-labelledby="attention-h" class="mb-8">
      <h2 id="attention-h" class="mb-3 text-heading">Needs attention</h2>
      <div class="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(260px,1fr))]">
        <AttentionCard
          v-for="alert in openAlerts.slice(0, 3)"
          :key="alert.id"
          kind="Budget alert"
          icon="payments"
          tone="warn"
          :title="alert.scopeName"
          :body="`${SCOPE_LABEL[alert.scope]} budget passed ${alert.thresholdPercent}%: ${formatSek(alert.spentSek)} of ${formatSek(alert.limitSek)}.`"
          action="Review"
          @action="router.push({ name: 'budgets' })"
        />
        <AttentionCard
          v-for="provider in openCircuits.slice(0, Math.max(0, 3 - openAlerts.length))"
          :key="provider.id"
          kind="Circuit open"
          icon="bolt"
          tone="danger"
          :title="provider.name"
          body="Requests are not sent to this provider until the circuit closes."
          action="Open operations"
          @action="router.push({ name: 'ops' })"
        />
      </div>
      <p v-if="attentionCount > 3" class="mt-3 text-small text-fg-2">
        {{ plural(attentionCount - 3, 'more item') }}.
        <router-link v-if="openAlerts.length > 3" :to="{ name: 'budgets' }" class="font-medium text-accent-ink underline">See all alerts</router-link>
      </p>
    </section>

    <section aria-labelledby="figures-h" class="mb-8">
      <h2 id="figures-h" class="sr-only">Key figures</h2>
      <div class="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(220px,1fr))]">
        <MetricTile
          label="Spend this month"
          :value="formatSek(month?.totalCostSek)"
          :meter="budgetPercent"
          meter-label="Share of department budgets used"
          :context="budgetPercent === null ? 'Estimate, not an invoice' : `${formatPercent(budgetPercent)} of department budgets`"
        />
        <MetricTile label="Requests, 24 h" :value="formatNumber(last24h?.totalRequests)" :context="`${formatNumber(last24h?.totalErrors)} errors`" />
        <MetricTile label="Error rate, 24 h" :value="formatPercent(errorRate)" :context="errorRate === null ? 'No traffic in 24 h' : 'Failed requests of all requests'" />
        <MetricTile
          label="Latency p95"
          :value="formatMs(p95)"
          :context="auth.isGatewayAdmin ? (p95 === null ? 'No traffic in 24 h' : 'Slowest provider, 24 h') : 'Visible to gateway admins'"
        />
      </div>
    </section>

    <div class="mb-8 grid items-start gap-3 [grid-template-columns:repeat(auto-fit,minmax(320px,1fr))]">
      <UiCard title="Daily spend" meta="Last 30 days" heading-id="daily-h">
        <DailySpendChart :points="points" />
      </UiCard>
      <UiCard v-if="auth.isGatewayAdmin" title="Providers" meta="p95, 24 h" heading-id="providers-h">
        <AsyncState :loading="!ops.health" :error="ops.healthError" :empty="false" @retry="ops.loadHealth">
          <DataTable
            :columns="providerColumns"
            :rows="providerRows"
            :row-key="(r) => r.id"
            caption="Provider health"
            empty-text="No providers are enabled."
          >
            <template #cell-name="{ row }"><span class="font-mono font-medium">{{ row.name }}</span></template>
            <template #cell-status="{ row }">
              <UiBadge :tone="providerStatus(row.circuitState, row.errorRate24h).tone" :label="providerStatus(row.circuitState, row.errorRate24h).label" />
            </template>
            <template #cell-p95="{ row }">
              <span :title="row.p95LatencyMs === null ? 'No traffic in 24 h' : undefined">{{ formatMs(row.p95LatencyMs) }}</span>
            </template>
          </DataTable>
        </AsyncState>
      </UiCard>
    </div>

    <section aria-labelledby="dept-h">
      <h2 id="dept-h" class="mb-3 text-heading">Spend by department</h2>
      <DataTable
        :columns="columns"
        :rows="month?.rows ?? []"
        :row-key="(r) => r.key"
        caption="Spend by department this month"
        empty-text="No spend recorded this month."
      >
        <template #cell-label="{ row }"><span lang="sv" class="font-medium">{{ row.label }}</span></template>
        <template #cell-requests="{ row }">{{ formatNumber(row.requests) }}</template>
        <template #cell-costSek="{ row }">{{ formatSek(row.costSek) }}</template>
      </DataTable>
    </section>
  </AsyncState>
</template>
