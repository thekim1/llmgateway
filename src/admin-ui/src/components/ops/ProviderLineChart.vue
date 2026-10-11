<script setup lang="ts">
import { computed, ref } from 'vue'
import UiButton from '@/components/ui/UiButton.vue'

export interface LineSeries {
  id: string
  name: string
  /** A CSS colour, e.g. `var(--series-1)`; the colour follows the provider, not its rank. */
  color: string
  /** One value per slot; null where the provider had no traffic. */
  values: (number | null)[]
}

const props = withDefaults(
  defineProps<{
    /** What the chart shows, e.g. "p95 latency". */
    label: string
    /** Bucket start times (epoch ms), oldest first. */
    slots: number[]
    series: LineSeries[]
    format: (value: number) => string
    formatSlot: (slot: number) => string
    /** The y axis reaches at least this value (keeps a near-zero error rate from filling the chart). */
    minMax?: number
  }>(),
  { minMax: 0 },
)

const W = 600
const H = 200
const asTable = ref(false)
const hover = ref<number | null>(null)

const max = computed(() => {
  const values = props.series.flatMap((s) => s.values.filter((v): v is number => v !== null))
  return Math.max(props.minMax, ...values, Number.EPSILON)
})
const x = (i: number) => (props.slots.length <= 1 ? W / 2 : (i / (props.slots.length - 1)) * W)
const y = (v: number) => H - (v / max.value) * H

/** Lines break where a provider had no traffic; a lone point becomes a dot (a zero-length round-capped segment). */
function path(values: (number | null)[]): string {
  const parts: string[] = []
  let run: string[] = []
  const flush = () => {
    if (run.length === 1) parts.push(`${run[0]} l 0.01 0`)
    else if (run.length > 1) parts.push(run.join(' '))
    run = []
  }
  values.forEach((v, i) => {
    if (v === null) return flush()
    run.push(`${run.length === 0 ? 'M' : 'L'} ${x(i).toFixed(1)} ${y(v).toFixed(1)}`)
  })
  flush()
  return parts.join(' ')
}

const lines = computed(() => props.series.map((s) => ({ ...s, d: path(s.values) })))
const hasData = computed(() => props.series.some((s) => s.values.some((v) => v !== null)))

const summary = computed(() => {
  if (!hasData.value) return `${props.label}: no traffic in this period.`
  const parts = [`${props.label} per provider, ${props.slots.length} periods from ${props.formatSlot(props.slots[0]!)} to ${props.formatSlot(props.slots[props.slots.length - 1]!)}.`]
  for (const s of props.series) {
    let peak: number | null = null
    let peakAt = 0
    let latest: number | null = null
    s.values.forEach((v, i) => {
      if (v === null) return
      latest = v
      if (peak === null || v > peak) {
        peak = v
        peakAt = i
      }
    })
    parts.push(
      peak === null
        ? `${s.name}: no traffic.`
        : `${s.name}: highest ${props.format(peak)} at ${props.formatSlot(props.slots[peakAt]!)}, latest ${props.format(latest!)}.`,
    )
  }
  return parts.join(' ')
})

function onMove(event: MouseEvent): void {
  const box = (event.currentTarget as HTMLElement).getBoundingClientRect()
  if (box.width <= 0 || props.slots.length === 0) return
  const ratio = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width))
  hover.value = Math.round(ratio * (props.slots.length - 1))
}
const hoverLeft = computed(() => (hover.value === null ? 0 : (x(hover.value) / W) * 100))
</script>

<template>
  <div class="flex flex-col gap-3">
    <ul v-if="series.length > 1" class="m-0 flex list-none flex-wrap gap-x-4 gap-y-1 p-0 text-small text-fg-2" :aria-label="`${label} legend`">
      <li v-for="s in series" :key="s.id" class="inline-flex items-center gap-1.5">
        <span class="inline-block h-0.5 w-4 rounded-full" :style="{ background: s.color }" aria-hidden="true" />{{ s.name }}
      </li>
    </ul>

    <div v-if="!asTable">
      <div class="flex gap-2">
        <div class="flex w-14 shrink-0 flex-col justify-between text-right text-caption text-fg-3" aria-hidden="true">
          <span>{{ format(max) }}</span>
          <span>{{ format(max / 2) }}</span>
          <span>{{ format(0) }}</span>
        </div>
        <!-- Pointer-only crosshair; the same values are in the text summary and the table view, which keyboard and screen reader users get. -->
        <!-- eslint-disable-next-line vuejs-accessibility/no-static-element-interactions, vuejs-accessibility/mouse-events-have-key-events -->
        <div class="relative h-48 min-w-0 flex-1" @mousemove="onMove" @mouseleave="hover = null">
          <svg role="img" :aria-label="summary" :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" class="block h-full w-full overflow-visible">
            <line v-for="g in [0, 0.5, 1]" :key="g" x1="0" :x2="W" :y1="H * g" :y2="H * g" stroke="var(--border)" stroke-width="1" vector-effect="non-scaling-stroke" />
            <path
              v-for="s in lines"
              :key="s.id"
              :d="s.d"
              fill="none"
              :stroke="s.color"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              vector-effect="non-scaling-stroke"
            />
          </svg>
          <template v-if="hover !== null">
            <div class="pointer-events-none absolute inset-y-0 w-px bg-fg-3" :style="{ left: `${hoverLeft}%` }" aria-hidden="true" />
            <div
              class="pointer-events-none absolute top-0 z-10 min-w-40 rounded-control bg-surface p-2 text-small shadow-2"
              :style="hoverLeft > 60 ? { right: `${100 - hoverLeft}%`, marginRight: '8px' } : { left: `${hoverLeft}%`, marginLeft: '8px' }"
              aria-hidden="true"
            >
              <p class="mb-1 font-medium">{{ formatSlot(slots[hover]!) }}</p>
              <p v-for="s in series" :key="s.id" class="flex items-center justify-between gap-3">
                <span class="inline-flex items-center gap-1.5"><span class="inline-block size-2 rounded-full" :style="{ background: s.color }" />{{ s.name }}</span>
                <span class="tabular">{{ s.values[hover] === null ? '—' : format(s.values[hover]!) }}</span>
              </p>
            </div>
          </template>
        </div>
      </div>
      <div v-if="slots.length" class="mt-1 flex justify-between pl-16 text-caption text-fg-3" aria-hidden="true">
        <span>{{ formatSlot(slots[0]!) }}</span>
        <span>{{ formatSlot(slots[slots.length - 1]!) }}</span>
      </div>
      <p class="mt-2 text-small text-fg-3">{{ hasData ? 'Gaps mean the provider had no traffic in that period.' : 'No traffic in this period.' }}</p>
    </div>
    <div v-else class="max-h-80 overflow-auto">
      <table class="w-full text-body">
        <caption class="sr-only">{{ label }} per provider</caption>
        <thead class="text-caption text-fg-3">
          <tr>
            <th scope="col" class="py-1 text-left font-medium">Period</th>
            <th v-for="s in series" :key="s.id" scope="col" class="py-1 text-right font-medium">{{ s.name }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(slot, i) in slots" :key="slot" class="border-t border-border">
            <th scope="row" class="py-1 text-left font-normal">{{ formatSlot(slot) }}</th>
            <td v-for="s in series" :key="s.id" class="tabular py-1 text-right">{{ s.values[i] === null ? '—' : format(s.values[i]!) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div>
      <UiButton size="sm" variant="quiet" :icon="asTable ? 'bar_chart' : 'table'" @click="asTable = !asTable">{{ asTable ? 'View chart' : 'View as table' }}</UiButton>
    </div>
  </div>
</template>
