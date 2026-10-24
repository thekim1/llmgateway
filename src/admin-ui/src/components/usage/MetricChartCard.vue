<script setup lang="ts">
import { ref, useId } from 'vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import TimeSeriesChart, { type ChartSeries } from './TimeSeriesChart.vue'

const props = withDefaults(
  defineProps<{
    title: string
    /** The headline figure, e.g. "Total" and "24,32 US$". */
    totalLabel: string
    total: string
    /** Extra text after the headline, e.g. a share or the number of models. */
    context?: string
    slots: number[]
    series: ChartSeries[]
    format: (value: number) => string
    formatTick?: (value: number) => string
    formatSlot: (slot: number) => string
    kind?: 'bar' | 'line'
    stacked?: boolean
    /** Shown instead of the chart when the period has no data for it. */
    emptyText?: string
  }>(),
  { context: undefined, formatTick: undefined, kind: 'bar', stacked: false, emptyText: 'No data in this period.' },
)

const view = ref<'bar' | 'line' | 'table'>(props.kind)
const headingId = `metric-${useId()}`
const hasData = () => props.series.some((s) => s.values.some((v) => v !== null && v !== 0))
const views = [
  { value: 'bar', icon: 'bar_chart', label: 'Bar chart' },
  { value: 'line', icon: 'show_chart', label: 'Line chart' },
  { value: 'table', icon: 'table', label: 'Table' },
] as const
</script>

<template>
  <section class="material flex min-w-0 flex-col gap-3 rounded-card p-5" :aria-labelledby="headingId">
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h3 :id="headingId" class="text-heading">{{ title }}</h3>
        <p class="mt-1 text-small text-fg-2">
          {{ totalLabel }} <span class="text-body-lg font-semibold text-fg">{{ total }}</span>
        </p>
        <p class="min-h-[18px] text-small text-fg-3"><slot name="context">{{ context }}</slot></p>
      </div>
      <div role="group" :aria-label="`${title} view`" class="flex shrink-0 gap-0.5 rounded-control bg-sunken p-[3px]">
        <button
          v-for="v in views"
          :key="v.value"
          type="button"
          class="inline-flex size-7 items-center justify-center rounded-chip"
          :class="view === v.value ? 'bg-surface text-fg shadow-1' : 'text-fg-3 hover:text-fg'"
          :aria-pressed="view === v.value"
          :aria-label="v.label"
          :title="v.label"
          @click="view = v.value"
        >
          <AppIcon :name="v.icon" :size="18" />
        </button>
      </div>
    </div>
    <ul v-if="series.length > 1" class="m-0 flex list-none flex-wrap gap-x-3 gap-y-1 p-0 text-small text-fg-2" :aria-label="`${title} legend`">
      <li v-for="s in series" :key="s.id" class="inline-flex min-w-0 items-center gap-1.5">
        <span class="inline-block size-2 shrink-0 rounded-full" :style="{ background: s.color }" aria-hidden="true" /><span class="max-w-48 truncate">{{ s.name }}</span>
      </li>
    </ul>
    <p v-if="!hasData()" class="mt-auto flex h-[196px] items-center justify-center text-small text-fg-3">{{ emptyText }}</p>
    <!-- Pinned to the bottom so the axes of cards in one row line up whatever the legend's height. -->
    <TimeSeriesChart
      v-else
      class="mt-auto"
      :label="title"
      :slots="slots"
      :series="series"
      :kind="view === 'table' ? kind : view"
      :stacked="stacked"
      :as-table="view === 'table'"
      :format="format"
      :format-tick="formatTick"
      :format-slot="formatSlot"
    />
  </section>
</template>
