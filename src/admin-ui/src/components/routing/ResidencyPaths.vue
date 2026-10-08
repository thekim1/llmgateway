<script setup lang="ts">
import { computed } from 'vue'
import type { DataResidency, Route } from '@/api/types'
import DataTable, { type Column } from '../ui/DataTable.vue'
import AppIcon from '../ui/AppIcon.vue'
import { buildTiers } from './tiers'

const props = defineProps<{ route: Route }>()

const columns: Column[] = [
  { key: 'who', label: 'Key allows' },
  { key: 'result', label: 'Requests land on' },
]

const scenarios: { id: string; label: string; allowed: DataResidency[] | null }[] = [
  { id: 'any', label: 'Any residency', allowed: null },
  { id: 'eu', label: 'EU only', allowed: ['Eu'] },
  { id: 'onprem', label: 'On-prem only', allowed: ['OnPrem'] },
]

const rows = computed(() => scenarios.map((s) => ({ ...s, tiers: buildTiers(props.route, s.allowed) })))
</script>

<template>
  <DataTable :columns="columns" :rows="rows" :row-key="(r) => r.id" caption="Where requests land, by key data residency">
    <template #cell-who="{ row }"><span class="font-medium">{{ row.label }}</span></template>
    <template #cell-result="{ row }">
      <span v-if="row.tiers.length === 0" class="inline-flex items-center gap-1.5 text-danger">
        <AppIcon name="error" :size="18" filled /><code class="font-mono">no_eligible_provider</code>
      </span>
      <ul v-else class="m-0 flex list-none flex-col gap-0.5 p-0">
        <li v-for="tier in row.tiers" :key="tier.priority">
          <span class="text-fg-3">{{ tier.position === 1 ? 'First' : 'Then' }}: </span>
          <template v-for="(item, i) in tier.targets" :key="item.target.modelId">
            <template v-if="i > 0">, </template>
            <span class="font-mono">{{ item.target.modelName }}</span> <span class="text-fg-3">{{ item.share }}%</span>
          </template>
        </li>
      </ul>
    </template>
  </DataTable>
</template>
