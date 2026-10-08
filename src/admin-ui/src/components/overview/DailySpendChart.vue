<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatSek, formatShortDate } from '@/utils/format'
import UiButton from '@/components/ui/UiButton.vue'

export interface DailyPoint {
  day: string
  costSek: number
}

const props = defineProps<{ points: DailyPoint[] }>()

const asTable = ref(false)
const max = computed(() => Math.max(1, ...props.points.map((p) => p.costSek)))
const total = computed(() => props.points.reduce((sum, p) => sum + p.costSek, 0))
const peak = computed(() => props.points.reduce<DailyPoint | null>((best, p) => (!best || p.costSek > best.costSek ? p : best), null))
const last = computed(() => props.points[props.points.length - 1] ?? null)
const summary = computed(() => {
  if (props.points.length === 0) return 'No spend recorded in the last 30 days.'
  const parts = [`Daily spend over ${props.points.length} days, ${formatSek(total.value)} in total.`]
  if (peak.value) parts.push(`Highest day ${formatShortDate(peak.value.day)} at ${formatSek(peak.value.costSek)}.`)
  if (last.value) parts.push(`Latest day ${formatShortDate(last.value.day)} at ${formatSek(last.value.costSek)}.`)
  return parts.join(' ')
})
</script>

<template>
  <div class="flex flex-col gap-3">
    <div v-if="!asTable">
      <div role="img" :aria-label="summary" class="flex h-40 items-end gap-1">
        <div
          v-for="(point, i) in points"
          :key="point.day"
          class="min-h-px flex-1 rounded-t-chip"
          :class="i === points.length - 1 ? 'bg-chart' : 'bg-chart-muted'"
          :style="{ height: `${Math.max(1, (point.costSek / max) * 100)}%` }"
          :title="`${formatShortDate(point.day)}: ${formatSek(point.costSek)}`"
        />
      </div>
      <p class="mt-2 text-small text-fg-3">{{ summary }}</p>
    </div>
    <div v-else class="max-h-64 overflow-auto">
      <table class="w-full text-body">
        <caption class="sr-only">Daily spend, last 30 days</caption>
        <thead class="text-caption text-fg-3">
          <tr>
            <th scope="col" class="py-1 text-left font-medium">Day</th>
            <th scope="col" class="py-1 text-right font-medium">Spend</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="point in points" :key="point.day" class="border-t border-border">
            <th scope="row" class="py-1 text-left font-normal">{{ formatShortDate(point.day) }}</th>
            <td class="tabular py-1 text-right">{{ formatSek(point.costSek) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div><UiButton size="sm" variant="quiet" :icon="asTable ? 'bar_chart' : 'table'" @click="asTable = !asTable">{{ asTable ? 'View chart' : 'View as table' }}</UiButton></div>
  </div>
</template>
