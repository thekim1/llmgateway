<script setup lang="ts">
import { computed } from 'vue'
import { clampPercent } from '@/utils/format'
import type { BarRow } from '@/components/types'

/**
 * Accessible bar visualisation: a real data table where one column also shows a proportional bar.
 * The bar is decorative (aria-hidden); the table carries the data, so the chart and its
 * text equivalent are the same element.
 */
const props = withDefaults(
  defineProps<{
    id: string
    caption: string
    labelHeader: string
    valueHeader: string
    shareHeader: string
    rows: BarRow[]
    extraHeaders?: string[]
    /** Value that equals a full bar. Defaults to the largest value. */
    max?: number
    series?: 1 | 2 | 3 | 4
  }>(),
  { extraHeaders: () => [], max: undefined, series: 1 },
)

const maxValue = computed(() => props.max ?? Math.max(0, ...props.rows.map((r) => r.value)))
const total = computed(() => props.rows.reduce((sum, r) => sum + r.value, 0))

function width(value: number): number {
  return maxValue.value > 0 ? clampPercent((value / maxValue.value) * 100) : 0
}

function share(value: number): string {
  if (total.value <= 0) return '0 %'
  return `${new Intl.NumberFormat('sv-SE', { maximumFractionDigits: 1 }).format((value / total.value) * 100)} %`
}
</script>

<template>
  <!-- eslint-disable-next-line vuejs-accessibility/no-noninteractive-tabindex -- scrollable region must be keyboard reachable -->
  <div class="table-scroll" role="region" :aria-labelledby="`${id}-caption`" tabindex="0">
    <table :id="id" class="table bar-table">
      <caption :id="`${id}-caption`">{{ caption }}</caption>
      <thead>
        <tr>
          <th scope="col">{{ labelHeader }}</th>
          <th v-for="header in extraHeaders" :key="header" scope="col" class="num">{{ header }}</th>
          <th scope="col" class="num">{{ valueHeader }}</th>
          <th scope="col" class="bar-table__bar-col">{{ shareHeader }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.key">
          <th scope="row">{{ row.label }}</th>
          <td v-for="(cell, index) in row.extra ?? []" :key="index" class="num">{{ cell }}</td>
          <td class="num">{{ row.valueText }}</td>
          <td class="bar-table__bar-cell">
            <span class="bar" aria-hidden="true">
              <span class="bar__fill" :class="`bar__fill--${series}`" :style="{ width: `${width(row.value)}%` }" />
            </span>
            <span class="bar-table__share">{{ share(row.value) }}</span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
