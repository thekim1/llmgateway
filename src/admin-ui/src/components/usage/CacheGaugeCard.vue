<script setup lang="ts">
import { computed, useId } from 'vue'
import { formatCompact, formatRatio } from '@/utils/format'

const props = defineProps<{
  /** Cached ÷ input tokens (0-1); null without input tokens. */
  rate: number | null
  cachedTokens: number
  inputTokens: number
}>()

const headingId = `cache-${useId()}`
// A half circle from the left (0 %) over the top to the right (100 %).
const R = 80
const CX = 100
const CY = 96
const arc = (from: number, to: number) => {
  const point = (t: number) => {
    const angle = Math.PI * (1 - t)
    return `${(CX + R * Math.cos(angle)).toFixed(2)} ${(CY - R * Math.sin(angle)).toFixed(2)}`
  }
  return `M ${point(from)} A ${R} ${R} 0 0 1 ${point(to)}`
}
const value = computed(() => Math.max(0, Math.min(1, props.rate ?? 0)))
</script>

<template>
  <section class="material flex min-w-0 flex-col gap-3 rounded-card p-5" :aria-labelledby="headingId">
    <div>
      <h3 :id="headingId" class="text-heading">Provider cache hit rate</h3>
      <p class="mt-1 text-small text-fg-2">Share of input tokens the providers served from their prompt cache</p>
    </div>
    <p v-if="rate === null" class="flex h-[196px] items-center justify-center text-small text-fg-3">No input tokens in this period.</p>
    <div v-else class="flex flex-1 flex-col items-center justify-center gap-2">
      <svg
        viewBox="0 0 200 108"
        class="block w-full max-w-60"
        role="meter"
        aria-label="Provider cache hit rate"
        :aria-valuenow="Math.round(value * 100)"
        aria-valuemin="0"
        aria-valuemax="100"
        :aria-valuetext="formatRatio(rate)"
      >
        <path :d="arc(0, 1)" fill="none" stroke="var(--chart-muted)" stroke-width="18" stroke-linecap="round" />
        <path v-if="value > 0" :d="arc(0, Math.max(value, 0.005))" fill="none" stroke="var(--chart)" stroke-width="18" stroke-linecap="round" />
      </svg>
      <p class="-mt-12 text-metric">{{ formatRatio(rate) }}</p>
      <p class="flex flex-wrap justify-center gap-x-4 text-small text-fg-2">
        <span class="inline-flex items-center gap-1.5"><span class="inline-block size-2 rounded-full bg-chart" aria-hidden="true" />Cached {{ formatCompact(cachedTokens) }}</span>
        <span class="inline-flex items-center gap-1.5"><span class="inline-block size-2 rounded-full bg-chart-muted" aria-hidden="true" />Input {{ formatCompact(inputTokens) }}</span>
      </p>
    </div>
  </section>
</template>
