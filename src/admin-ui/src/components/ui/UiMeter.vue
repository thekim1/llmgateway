<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    /** Percent used (can exceed 100). */
    value: number
    label: string
    /** Alert thresholds in percent, drawn as ticks. */
    ticks?: number[]
    /** Print the percentage next to the meter. */
    showValue?: boolean
    thin?: boolean
  }>(),
  { ticks: () => [], showValue: false },
)

const clamped = computed(() => Math.max(0, Math.min(100, props.value)))
const fill = computed(() => (props.value >= 100 ? 'bg-danger' : props.value >= 80 ? 'bg-warn' : 'bg-chart'))
const text = computed(() => `${Math.round(props.value)}%`)
</script>

<template>
  <div class="flex items-center gap-3">
    <div
      role="meter"
      :aria-label="label"
      :aria-valuenow="Math.round(clamped)"
      aria-valuemin="0"
      aria-valuemax="100"
      :aria-valuetext="text"
      class="relative min-w-0 flex-1 overflow-hidden rounded-full bg-sunken"
      :class="thin ? 'h-1.5' : 'h-2'"
    >
      <div class="h-full rounded-full transition-[width] duration-base ease-emphasized" :class="fill" :style="{ width: `${clamped}%` }" />
      <div v-for="tick in ticks" :key="tick" aria-hidden="true" class="absolute inset-y-0 w-0.5 bg-fg-3 opacity-50" :style="{ left: `${Math.min(tick, 100)}%` }" />
    </div>
    <span v-if="showValue" class="tabular w-10 text-right text-small font-medium">{{ text }}</span>
  </div>
</template>
