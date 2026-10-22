<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { GATEWAY_ENDPOINTS, REQUEST_OUTCOMES, type GatewayEndpointName, type RequestOutcome, type UsageMetrics, type UsageMetricsBucket, type UsageMetricsQuery } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useProvidersStore } from '@/stores/providers'
import { useTeamsStore } from '@/stores/teams'
import { useUsageStore } from '@/stores/usage'
import { useModelOptions } from '@/composables/useModelOptions'
import { OUTCOME_LABEL } from '@/utils/labels'
import {
  formatCompact,
  formatDate,
  formatMs,
  formatNumber,
  formatRatio,
  formatShortDate,
  formatTime,
  formatTokensPerSecond,
  formatUsdCost,
  todayIso,
  toIsoDate,
} from '@/utils/format'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import CacheGaugeCard from './CacheGaugeCard.vue'
import MetricChartCard from './MetricChartCard.vue'
import type { ChartSeries } from './TimeSeriesChart.vue'

type Period = '24h' | '7d' | '30d' | '90d' | 'custom'
const DAY_MS = 86_400_000
const PERIOD_DAYS: Record<Exclude<Period, 'custom'>, number> = { '24h': 1, '7d': 7, '30d': 30, '90d': 90 }
const periods: { value: Period; label: string }[] = [
  { value: '24h', label: '24 hours' },
  { value: '7d', label: '7 days' },
  { value: '30d', label: '30 days' },
  { value: '90d', label: '90 days' },
  { value: 'custom', label: 'Custom' },
]
const ENDPOINT_LABEL: Record<GatewayEndpointName, string> = {
  ChatCompletions: 'Chat completions',
  Responses: 'Responses',
  AnthropicMessages: 'Anthropic messages',
  Embeddings: 'Embeddings',
  AudioTranscriptions: 'Audio transcriptions',
  AudioTranslations: 'Audio translations',
  Realtime: 'Live audio',
  RealtimeTranslations: 'Live translation',
  ImageGenerations: 'Image generation',
  ImageEdits: 'Image edits',
  AnthropicCountTokens: 'Token counting',
}

const auth = useAuthStore()
const usage = useUsageStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const providers = useProvidersStore()
const { options: modelOptions } = useModelOptions()

const period = ref<Period>('7d')
const from = ref(toIsoDate(new Date(Date.now() - 7 * DAY_MS)))
const to = ref(todayIso())
const departmentId = ref('')
const teamId = ref('')
const keyId = ref('')
const model = ref('')
const provider = ref('')
const outcome = ref<RequestOutcome | ''>('')
const endpoint = ref<GatewayEndpointName | ''>('')
const rangeError = ref<string | null>(null)

const departmentOptions = computed(() => [{ value: '', label: 'All' }, ...departments.items.map((d) => ({ value: d.id, label: d.name }))])
const teamOptions = computed(() => [
  { value: '', label: 'All' },
  ...teams.items.filter((t) => !departmentId.value || t.departmentId === departmentId.value).map((t) => ({ value: t.id, label: t.name })),
])
const keyOptions = computed(() => [
  { value: '', label: 'All' },
  ...keys.items
    .filter((k) => (!departmentId.value || k.departmentId === departmentId.value) && (!teamId.value || k.teamId === teamId.value))
    .map((k) => ({ value: k.id, label: k.name })),
])
const modelSelectOptions = computed(() => {
  const names = new Set(modelOptions.value.map((m) => m.name))
  if (model.value) names.add(model.value)
  return [{ value: '', label: 'All' }, ...[...names].sort((a, b) => a.localeCompare(b)).map((n) => ({ value: n, label: n }))]
})
const providerOptions = computed(() => [{ value: '', label: 'All' }, ...providers.items.map((p) => ({ value: p.name, label: p.displayName || p.name }))])
const outcomeOptions = [{ value: '', label: 'All' }, ...REQUEST_OUTCOMES.map((o) => ({ value: o, label: OUTCOME_LABEL[o].label }))]
const endpointOptions = [{ value: '', label: 'All' }, ...GATEWAY_ENDPOINTS.map((e) => ({ value: e, label: ENDPOINT_LABEL[e] }))]
const filtered = computed(() => !!(departmentId.value || teamId.value || keyId.value || model.value || provider.value || outcome.value || endpoint.value))

function clearFilters(): void {
  departmentId.value = ''
  teamId.value = ''
  keyId.value = ''
  model.value = ''
  provider.value = ''
  outcome.value = ''
  endpoint.value = ''
}

function range(): { from: string; to: string } | null {
  if (period.value !== 'custom') {
    const now = Date.now()
    return { from: new Date(now - PERIOD_DAYS[period.value] * DAY_MS).toISOString(), to: new Date(now).toISOString() }
  }
  if (!from.value || !to.value) {
    rangeError.value = 'Choose both a start and an end date.'
    return null
  }
  if (from.value > to.value) {
    rangeError.value = 'The start date must be on or before the end date.'
    return null
  }
  return { from: new Date(`${from.value}T00:00:00`).toISOString(), to: new Date(`${to.value}T23:59:59.999`).toISOString() }
}

function load(): void {
  rangeError.value = null
  const dates = range()
  if (!dates) return
  const query: UsageMetricsQuery = {
    ...dates,
    ...(departmentId.value && { departmentId: departmentId.value }),
    ...(teamId.value && { teamId: teamId.value }),
    ...(keyId.value && { keyId: keyId.value }),
    ...(model.value && { model: model.value }),
    ...(provider.value && { provider: provider.value }),
    ...(outcome.value && { outcome: outcome.value }),
    ...(endpoint.value && { endpoint: endpoint.value }),
  }
  void usage.loadMetrics(query)
}

watch(departmentId, () => {
  if (teamId.value && !teams.items.some((t) => t.id === teamId.value && t.departmentId === departmentId.value)) teamId.value = ''
})
watch(teamId, () => {
  if (keyId.value && !keys.items.some((k) => k.id === keyId.value && (!teamId.value || k.teamId === teamId.value))) keyId.value = ''
})
watch([period, departmentId, teamId, keyId, model, provider, outcome, endpoint], () => {
  if (period.value !== 'custom') load()
})

onMounted(() => {
  if (auth.isGatewayAdmin && !providers.loaded) void providers.load()
  load()
})

// ---- chart data ------------------------------------------------------------------------------------------

const data = computed<UsageMetrics | null>(() => usage.metrics)
const slots = computed(() => data.value?.buckets.map((b) => new Date(b.start).getTime()) ?? [])
const formatSlot = (ms: number) =>
  data.value?.bucket === 'hour' ? `${formatShortDate(new Date(ms))} ${formatTime(new Date(ms))}` : formatShortDate(new Date(ms))
const pick = (f: (b: UsageMetricsBucket) => number | null): (number | null)[] => data.value?.buckets.map(f) ?? []

/** Models keep the same colour in the cost and the requests chart; colours go by name, "other models" is grey. */
const modelColors = computed(() => {
  const named = (data.value?.models ?? []).filter((m) => m.model !== null).map((m) => m.model!).sort((a, b) => a.localeCompare(b))
  return new Map(named.map((name, i) => [name, `var(--series-${i + 1})`]))
})
function modelSeries(values: (m: UsageMetrics['models'][number]) => number[]): ChartSeries[] {
  return (data.value?.models ?? []).map((m) => ({
    id: m.model ?? '__other',
    name: m.model ?? 'Other models',
    color: m.model === null ? 'var(--border-input)' : (modelColors.value.get(m.model) ?? 'var(--border-input)'),
    values: values(m),
  }))
}
const costSeries = computed(() => modelSeries((m) => m.costUsdPerBucket))
const modelRequestSeries = computed(() => modelSeries((m) => m.requestsPerBucket))

const requestSeries = computed<ChartSeries[]>(() => [
  { id: 'success', name: 'Success', color: 'var(--ok)', values: pick((b) => b.successes) },
  { id: 'provider', name: 'Provider error', color: 'var(--danger)', values: pick((b) => b.providerErrors) },
  { id: 'refused', name: 'Refused', color: 'var(--warn)', values: pick((b) => b.refused) },
  { id: 'cancelled', name: 'Cancelled', color: 'var(--border-input)', values: pick((b) => b.cancelled) },
])
const tokenSeries = computed<ChartSeries[]>(() => [
  { id: 'input', name: 'Input', color: 'var(--series-1)', values: pick((b) => b.inputTokens - b.cachedInputTokens) },
  { id: 'cached', name: 'Cached input', color: 'var(--series-3)', values: pick((b) => b.cachedInputTokens) },
  { id: 'output', name: 'Output', color: 'var(--series-2)', values: pick((b) => b.outputTokens) },
])

/** Percentiles are one ordered measure: one hue from light to full; the average stands apart in its own hue. */
const shade = (percent: number) => `color-mix(in oklab, var(--chart) ${percent}%, var(--surface))`
function latencySeries(stats: (b: UsageMetricsBucket) => UsageMetricsBucket['latency']): ChartSeries[] {
  return [
    { id: 'avg', name: 'Avg', color: 'var(--series-2)', values: pick((b) => stats(b).avg) },
    { id: 'p50', name: 'P50', color: shade(55), values: pick((b) => stats(b).p50) },
    { id: 'p95', name: 'P95', color: shade(78), values: pick((b) => stats(b).p95) },
    { id: 'p99', name: 'P99', color: shade(100), values: pick((b) => stats(b).p99) },
  ]
}
const latency = computed(() => latencySeries((b) => b.latency))
const overhead = computed(() => latencySeries((b) => b.overhead))
const firstTokenSeries = computed<ChartSeries[]>(() => [
  { id: 'avg', name: 'Avg', color: 'var(--series-2)', values: pick((b) => b.firstTokenAvgMs) },
  { id: 'p95', name: 'P95', color: shade(100), values: pick((b) => b.firstTokenP95Ms) },
])
const throughputSeries = computed<ChartSeries[]>(() => [{ id: 'tps', name: 'Output tokens per second', color: 'var(--series-3)', values: pick((b) => b.tokensPerSecond) }])

const totals = computed(() => data.value?.totals ?? null)
const errorShare = computed(() => {
  const t = totals.value
  if (!t || t.requests === 0) return undefined
  return `${formatRatio((t.providerErrors + t.refused) / t.requests)} errors · ${formatRatio(t.fallbacks / t.requests)} fell back`
})
const modelCount = computed(() => {
  const named = data.value?.models.filter((m) => m.model !== null).length ?? 0
  return data.value?.models.some((m) => m.model === null) ? `top ${named} models and others` : `${named} ${named === 1 ? 'model' : 'models'}`
})
const percentiles = (s: { p95: number | null; p99?: number | null } | undefined) => (s?.p95 == null ? undefined : `P95 ${formatMs(s.p95)}`)
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-end justify-between gap-3">
      <SegmentedControl v-model="period" :options="periods" label="Time range" />
      <form v-if="period === 'custom'" class="flex flex-wrap items-end gap-3" novalidate @submit.prevent="load">
        <TextField v-model="from" label="From" type="date" />
        <TextField v-model="to" label="To" type="date" />
        <UiButton type="submit" variant="primary">Apply</UiButton>
      </form>
    </div>
    <InlineError v-if="rangeError" :message="rangeError" />

    <div class="flex flex-wrap items-center gap-x-4 gap-y-2" role="group" aria-label="Dashboard filters">
      <FilterSelect v-model="departmentId" label="Department" :options="departmentOptions" />
      <FilterSelect v-model="teamId" label="Team" :options="teamOptions" />
      <FilterSelect v-model="keyId" label="Key" :options="keyOptions" />
      <FilterSelect v-model="model" label="Model" :options="modelSelectOptions" />
      <FilterSelect v-if="auth.isGatewayAdmin" v-model="provider" label="Provider" :options="providerOptions" />
      <FilterSelect v-model="outcome" label="Outcome" :options="outcomeOptions" />
      <FilterSelect v-model="endpoint" label="Endpoint" :options="endpointOptions" />
      <UiButton v-if="filtered" size="sm" variant="quiet" icon="filter_alt_off" @click="clearFilters">Clear filters</UiButton>
    </div>

    <p v-if="data?.recordsFrom" class="flex items-start gap-2 text-small text-fg-2">
      <AppIcon name="info" :size="18" class="mt-px shrink-0" />
      The charts start on {{ formatDate(data.recordsFrom) }}: older usage is only kept as monthly totals (data retention, see Settings), without timings.
    </p>

    <AsyncState :loading="usage.metricsLoading && !data" :error="usage.metricsError" :empty="false" @retry="load">
      <div v-if="data && totals" class="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3" :class="{ 'opacity-60 transition-opacity': usage.metricsLoading }" :aria-busy="usage.metricsLoading">
        <MetricChartCard
          title="Cost"
          total-label="Total"
          :total="formatUsdCost(totals.costUsd)"
          :context="modelCount"
          :slots="slots"
          :series="costSeries"
          stacked
          :format="formatUsdCost"
          :format-slot="formatSlot"
          empty-text="No cost in this period."
        />
        <MetricChartCard
          title="Requests"
          total-label="Total"
          :total="formatNumber(totals.requests)"
          :context="errorShare"
          :slots="slots"
          :series="requestSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No requests in this period."
        />
        <MetricChartCard
          title="Tokens"
          total-label="Total"
          :total="formatCompact(totals.inputTokens + totals.outputTokens)"
          :slots="slots"
          :series="tokenSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No tokens in this period."
        />
        <CacheGaugeCard :rate="totals.cacheHitRate" :cached-tokens="totals.cachedInputTokens" :input-tokens="totals.inputTokens" />
        <MetricChartCard
          title="Requests by model"
          total-label="Total"
          :total="formatNumber(totals.requests)"
          :context="modelCount"
          :slots="slots"
          :series="modelRequestSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No requests in this period."
        />
        <MetricChartCard
          title="Latency"
          total-label="Avg"
          :total="formatMs(totals.latency.avg)"
          :context="percentiles(totals.latency)"
          :slots="slots"
          :series="latency"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No answered requests in this period."
        />
        <MetricChartCard
          title="Gateway overhead"
          total-label="Avg"
          :total="formatMs(totals.overhead.avg)"
          :context="percentiles(totals.overhead)"
          :slots="slots"
          :series="overhead"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No timings in this period. The gateway records them for requests made after this dashboard was added."
        />
        <MetricChartCard
          title="Time to first token"
          total-label="Avg"
          :total="formatMs(totals.firstToken.avg)"
          :context="percentiles(totals.firstToken)"
          :slots="slots"
          :series="firstTokenSeries"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No streamed answers in this period."
        />
        <MetricChartCard
          title="Throughput"
          total-label="Avg"
          :total="formatTokensPerSecond(totals.tokensPerSecond)"
          context="output tokens per second"
          :slots="slots"
          :series="throughputSeries"
          :format="formatTokensPerSecond"
          :format-tick="(v: number) => formatCompact(Math.round(v))"
          :format-slot="formatSlot"
          empty-text="No answers with output in this period."
        />
      </div>
    </AsyncState>
  </div>
</template>
