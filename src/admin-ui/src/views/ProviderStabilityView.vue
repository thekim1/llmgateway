<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { ProviderRank, ProviderStability } from '@/api/types'
import { formatMs, formatNumber, formatRatio, formatShortDate, formatTime, TIME_ZONE } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import ProviderLineChart, { type LineSeries } from '@/components/ops/ProviderLineChart.vue'

type Period = '24h' | '7d' | '30d'
const HOUR_MS = 3_600_000
const DAY_MS = 86_400_000
/** The categorical palette has eight validated hues; more providers than that are listed in the table only. */
const MAX_SERIES = 8

const periods: { value: Period; label: string }[] = [
  { value: '24h', label: '24 hours' },
  { value: '7d', label: '7 days' },
  { value: '30d', label: '30 days' },
]
const PERIOD_TEXT: Record<Period, string> = { '24h': 'last 24 hours, per hour', '7d': 'last 7 days, per hour', '30d': 'last 30 days, per day' }

const period = ref<Period>('24h')
const data = ref<ProviderStability | null>(null)
const loading = ref(false)
const error = ref<unknown>(null)

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  const now = Date.now()
  const span = period.value === '24h' ? DAY_MS : period.value === '7d' ? 7 * DAY_MS : 30 * DAY_MS
  try {
    data.value = await api.ops.providerStats({
      from: new Date(now - span).toISOString(),
      to: new Date(now).toISOString(),
      bucket: period.value === '30d' ? 'day' : 'hour',
    })
  } catch (e) {
    error.value = e
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(period, load)

/** The hour of `ms` in Stockholm, to keep day steps on local midnight across daylight-saving changes. */
const stockholmHour = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, hour: 'numeric', hourCycle: 'h23' })
function nextDay(ms: number): number {
  const next = ms + DAY_MS
  const hour = Number(stockholmHour.format(next))
  return hour === 23 ? next + HOUR_MS : hour === 1 ? next - HOUR_MS : next
}

/** Every bucket start in the period, so hours or days without traffic show as gaps. */
const slots = computed(() => {
  const d = data.value
  if (!d) return []
  const result: number[] = []
  const end = new Date(d.to).getTime()
  for (let t = new Date(d.from).getTime(); t < end && result.length < 2000; t = d.bucket === 'Hour' ? t + HOUR_MS : nextDay(t)) result.push(t)
  return result
})

const formatSlot = (ms: number) => (data.value?.bucket === 'Day' ? formatShortDate(new Date(ms)) : `${formatShortDate(new Date(ms))} ${formatTime(new Date(ms))}`)

/** Colours follow the provider (alphabetical order), not its rank, so a provider keeps its colour when the ranking changes. */
const charted = computed(() => {
  const providers = [...(data.value?.providers ?? [])].sort((a, b) => b.requests - a.requests).slice(0, MAX_SERIES)
  return providers.sort((a, b) => a.name.localeCompare(b.name)).map((p, i) => ({ provider: p, color: `var(--series-${i + 1})` }))
})
const hidden = computed(() => Math.max(0, (data.value?.providers.length ?? 0) - MAX_SERIES))

function series(pick: (b: ProviderStability['buckets'][number]) => number | null): LineSeries[] {
  const index = new Map(slots.value.map((s, i) => [s, i]))
  return charted.value.map(({ provider, color }) => {
    const values: (number | null)[] = slots.value.map(() => null)
    for (const b of data.value?.buckets ?? []) {
      if (b.providerId !== provider.providerId) continue
      const i = index.get(new Date(b.start).getTime())
      if (i !== undefined) values[i] = pick(b)
    }
    return { id: provider.providerId, name: provider.name, color, values }
  })
}
const latencySeries = computed(() => series((b) => b.p95LatencyMs))
const errorSeries = computed(() => series((b) => b.errorRate))

const columns: Column[] = [
  { key: 'rank', label: 'Rank', align: 'right' },
  { key: 'name', label: 'Provider' },
  { key: 'availability', label: 'Availability', align: 'right' },
  { key: 'providerErrors', label: 'Provider errors', align: 'right' },
  { key: 'requests', label: 'Requests', align: 'right' },
  { key: 'p50', label: 'p50', align: 'right' },
  { key: 'p95', label: 'p95', align: 'right' },
  { key: 'fallbackRate', label: 'Fallback rate', align: 'right' },
]
const ranking = computed(() => (data.value?.providers ?? []).map((p, i) => ({ ...p, rank: i + 1 })))
const rankLabel = (p: ProviderRank) => (p.exists ? p.name : `${p.name} (deleted)`)
</script>

<template>
  <PageHeader title="Provider stability" description="How often each provider fails and how fast it answers, over time.">
    <UiButton icon="refresh" :loading="loading" @click="load">Refresh</UiButton>
  </PageHeader>

  <div class="mb-6 flex flex-wrap items-center justify-between gap-3">
    <SegmentedControl v-model="period" :options="periods" label="Period" />
    <router-link :to="{ name: 'ops' }" class="text-small font-medium text-accent-ink underline">Back to operations</router-link>
  </div>

  <AsyncState :loading="loading" :error="error" :empty="!data" empty-text="No statistics yet." @retry="load">
    <div v-if="data" class="flex flex-col gap-6">
      <p class="text-small text-fg-2">
        Showing the {{ PERIOD_TEXT[period] }}<template v-if="data.bucket === 'Day'"> (days in Swedish time)</template>. Latency is the whole request as the client saw it,
        including fallback attempts and the full length of a stream; live audio sessions are left out. Provider errors are 5xx answers, timeouts and broken
        streams; client errors, cancellations and requests the gateway refused are not counted.
        <template v-if="hidden > 0"> The charts show the {{ MAX_SERIES }} busiest providers; the table lists all {{ data.providers.length }}.</template>
      </p>

      <div class="grid items-start gap-3 [grid-template-columns:repeat(auto-fit,minmax(min(420px,100%),1fr))]">
        <UiCard title="Latency p95" meta="Per provider" heading-id="stability-latency">
          <ProviderLineChart label="p95 latency" :slots="slots" :series="latencySeries" :format="formatMs" :format-slot="formatSlot" />
        </UiCard>
        <UiCard title="Provider error rate" meta="Share of requests" heading-id="stability-errors">
          <ProviderLineChart label="Provider error rate" :slots="slots" :series="errorSeries" :format="formatRatio" :format-slot="formatSlot" :min-max="0.05" />
        </UiCard>
      </div>

      <section aria-labelledby="stability-ranking" class="flex flex-col gap-3">
        <h2 id="stability-ranking" class="text-heading">Most stable first</h2>
        <DataTable
          :columns="columns"
          :rows="ranking"
          :row-key="(r) => r.providerId"
          caption="Providers ranked by availability, then p95 latency"
          empty-text="No provider traffic in this period."
        >
          <template #cell-rank="{ row }">{{ row.rank }}</template>
          <template #cell-name="{ row }"><span class="font-medium">{{ rankLabel(row) }}</span></template>
          <template #cell-availability="{ row }">{{ formatRatio(row.availability) }}</template>
          <template #cell-providerErrors="{ row }">{{ formatNumber(row.providerErrors) }}</template>
          <template #cell-requests="{ row }">{{ formatNumber(row.requests) }}</template>
          <template #cell-p50="{ row }">{{ formatMs(row.p50LatencyMs) }}</template>
          <template #cell-p95="{ row }">{{ formatMs(row.p95LatencyMs) }}</template>
          <template #cell-fallbackRate="{ row }">{{ formatRatio(row.fallbackRate) }}</template>
        </DataTable>
        <p class="text-small text-fg-3">
          Availability is the share of requests that did not fail at the provider. A request that failed at one provider and was answered by a fallback
          is recorded once, on the provider that answered; the fallback rate shows how often that happened.
        </p>
      </section>
    </div>
  </AsyncState>
</template>
