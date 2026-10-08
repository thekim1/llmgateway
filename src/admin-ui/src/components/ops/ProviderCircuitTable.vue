<script setup lang="ts">
import type { OpsProviderHealth } from '@/api/types'
import { CIRCUIT_LABEL } from '@/utils/labels'
import { formatMs, formatNumber, formatRatio } from '@/utils/format'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

defineProps<{ providers: OpsProviderHealth[] }>()
defineEmits<{ (e: 'toggle', provider: OpsProviderHealth): void }>()

const columns: Column[] = [
  { key: 'name', label: 'Provider' },
  { key: 'circuit', label: 'Circuit' },
  { key: 'requests', label: 'Requests (24 h)', align: 'right' },
  { key: 'errors', label: 'Errors (24 h)', align: 'right' },
  { key: 'p50', label: 'p50', align: 'right' },
  { key: 'p95', label: 'p95', align: 'right' },
  { key: 'actions', label: 'Actions', hideLabel: true, align: 'right' },
]
</script>

<template>
  <DataTable :columns="columns" :rows="providers" :row-key="(p) => p.id" caption="Provider circuit state and latency" empty-text="No providers configured.">
    <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
    <template #cell-circuit="{ row }">
      <UiBadge :tone="CIRCUIT_LABEL[row.circuitState].tone">{{ CIRCUIT_LABEL[row.circuitState].label }}</UiBadge>
    </template>
    <template #cell-requests="{ row }">{{ formatNumber(row.requests24h) }}</template>
    <template #cell-errors="{ row }">{{ row.requests24h === 0 ? '—' : formatRatio(row.errorRate24h) }}</template>
    <template #cell-p50="{ row }">
      <span v-if="row.p50LatencyMs !== null">{{ formatMs(row.p50LatencyMs) }}</span>
      <span v-else title="No traffic in 24 h">— <span class="sr-only">No traffic in 24 h</span></span>
    </template>
    <template #cell-p95="{ row }">
      <span v-if="row.p95LatencyMs !== null">{{ formatMs(row.p95LatencyMs) }}</span>
      <span v-else title="No traffic in 24 h">— <span class="sr-only">No traffic in 24 h</span></span>
    </template>
    <template #cell-actions="{ row }">
      <UiButton size="sm" :variant="row.circuitState === 'Open' ? 'secondary' : 'danger'" @click="$emit('toggle', row)">
        {{ row.circuitState === 'Open' ? 'Close circuit' : 'Open circuit' }}<span class="sr-only"> for {{ row.name }}</span>
      </UiButton>
    </template>
  </DataTable>
  <p class="mt-2 text-small text-fg-3">A dash means the provider had no traffic in the last 24 hours.</p>
</template>
