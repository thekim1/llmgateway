<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

export interface ChartSeries {
  id: string
  name: string
  /** A CSS colour, e.g. `var(--series-1)`; it follows the entity, not its rank. */
  color: string
  /** One value per slot; null where there is nothing to show. */
  values: (number | null)[]
}

const props = withDefaults(
  defineProps<{
    /** What the chart shows, e.g. "Latency". Used in the summary and the table caption. */
    label: string
    /** Bucket start times (epoch ms), oldest first. */
    slots: number[]
    series: ChartSeries[]
    kind?: 'bar' | 'line'
    /**
     * Bars of additive series (cost per model, requests by outcome) are stacked. Bars of non-additive series
     * (percentiles) are drawn on top of each other, the largest at the back.
     */
    stacked?: boolean
    asTable?: boolean
    format: (value: number) => string
    /** Axis tick text; defaults to `format`. */
    formatTick?: (value: number) => string
    formatSlot: (slot: number) => string
  }>(),
  { kind: 'bar', stacked: false, asTable: false, formatTick: undefined },
)

const H = 176
const BAR_MAX = 24
const width = ref(600)
const plot = ref<HTMLElement | null>(null)
const hover = ref<number | null>(null)
let observer: ResizeObserver | null = null

onMounted(() => {
  if (!plot.value || typeof ResizeObserver === 'undefined') return
  observer = new ResizeObserver(([entry]) => {
    if (entry && entry.contentRect.width > 0) width.value = entry.contentRect.width
  })
  observer.observe(plot.value)
})
onBeforeUnmount(() => observer?.disconnect())

const tick = computed(() => props.formatTick ?? props.format)
const hasData = computed(() => props.series.some((s) => s.values.some((v) => v !== null && v !== 0)))

/** Sum of the stacked values per slot, or the largest value. */
const slotTotals = computed(() =>
  props.slots.map((_, i) =>
    props.stacked && props.kind === 'bar'
      ? props.series.reduce((sum, s) => sum + (s.values[i] ?? 0), 0)
      : Math.max(0, ...props.series.map((s) => s.values[i] ?? 0)),
  ),
)

/** A clean axis: 0 and four steps of 1, 2, 2.5 or 5 × 10ⁿ. */
const axis = computed(() => {
  const peak = Math.max(0, ...slotTotals.value)
  if (peak <= 0) return { max: 1, ticks: [0, 0.25, 0.5, 0.75, 1] }
  const rough = peak / 4
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const step = ([1, 2, 2.5, 5, 10].find((m) => m * magnitude >= rough) ?? 10) * magnitude
  return { max: step * 4, ticks: [0, step, step * 2, step * 3, step * 4] }
})
const y = (v: number) => H - (v / axis.value.max) * H
const band = computed(() => width.value / Math.max(1, props.slots.length))
const center = (i: number) => band.value * (i + 0.5)
const barWidth = computed(() => Math.max(1, Math.min(BAR_MAX, band.value * 0.72)))
/** The 2 px surface gap and 4 px rounded data-end only when bars are wide enough to carry them. */
const roomy = computed(() => barWidth.value >= 6)

interface Rect {
  key: string
  d: string
  color: string
}

/** A bar segment; only the top segment of a column gets the rounded end, the baseline stays square. */
function rect(x: number, top: number, bottom: number, w: number, rounded: boolean): string {
  const h = Math.max(0, bottom - top)
  const r = rounded ? Math.min(4, w / 2, h) : 0
  if (r <= 0) return `M${x} ${bottom}V${top}H${x + w}V${bottom}Z`
  return `M${x} ${bottom}V${top + r}Q${x} ${top} ${x + r} ${top}H${x + w - r}Q${x + w} ${top} ${x + w} ${top + r}V${bottom}Z`
}

const bars = computed<Rect[]>(() => {
  if (props.kind !== 'bar') return []
  const out: Rect[] = []
  const w = barWidth.value
  const gap = roomy.value ? 2 : 0
  props.slots.forEach((slot, i) => {
    const x = center(i) - w / 2
    const present = props.series.map((s) => ({ s, v: s.values[i] ?? null })).filter((p): p is { s: ChartSeries; v: number } => p.v !== null && p.v > 0)
    if (props.stacked) {
      let base = H
      present.forEach((p, n) => {
        const height = (p.v / axis.value.max) * H
        const segmentTop = base - height
        const last = n === present.length - 1
        // The gap is taken from the segment's own height, so the column total keeps its height.
        const bottom = n === 0 ? base : base - gap
        out.push({ key: `${slot}-${p.s.id}`, d: rect(x, segmentTop, Math.max(segmentTop, bottom), w, last && roomy.value), color: p.s.color })
        base = segmentTop
      })
    } else {
      // Largest at the back so every series stays visible.
      ;[...present].sort((a, b) => b.v - a.v).forEach((p) => {
        out.push({ key: `${slot}-${p.s.id}`, d: rect(x, y(p.v), H, w, roomy.value), color: p.s.color })
      })
    }
  })
  return out
})

/** Lines break where a value is missing; a lone point becomes a dot. */
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
    run.push(`${run.length === 0 ? 'M' : 'L'} ${center(i).toFixed(1)} ${y(v).toFixed(1)}`)
  })
  flush()
  return parts.join(' ')
}
const lines = computed(() => (props.kind === 'line' ? props.series.map((s) => ({ ...s, d: path(s.values) })) : []))

const summary = computed(() => {
  if (!hasData.value) return `${props.label}: no data in this period.`
  const first = props.formatSlot(props.slots[0]!)
  const last = props.formatSlot(props.slots[props.slots.length - 1]!)
  const parts = [`${props.label}, ${props.slots.length} periods from ${first} to ${last}.`]
  for (const s of props.series) {
    let peak: number | null = null
    let peakAt = 0
    s.values.forEach((v, i) => {
      if (v !== null && (peak === null || v > peak)) {
        peak = v
        peakAt = i
      }
    })
    if (peak !== null) parts.push(`${s.name}: highest ${props.format(peak)} at ${props.formatSlot(props.slots[peakAt]!)}.`)
  }
  return parts.join(' ')
})

function onMove(event: MouseEvent): void {
  const box = (event.currentTarget as HTMLElement).getBoundingClientRect()
  if (box.width <= 0 || props.slots.length === 0) return
  const index = Math.floor(((event.clientX - box.left) / box.width) * props.slots.length)
  hover.value = Math.min(props.slots.length - 1, Math.max(0, index))
}
const hoverLeft = computed(() => (hover.value === null ? 0 : (center(hover.value) / width.value) * 100))
const hoverRows = computed(() => {
  const i = hover.value
  if (i === null) return []
  return props.series.map((s) => ({ ...s, value: s.values[i] ?? null }))
})
const xLabels = computed(() => {
  const n = props.slots.length
  if (n === 0) return []
  const picks = n === 1 ? [0] : n < 5 ? [0, n - 1] : [0, Math.floor((n - 1) / 2), n - 1]
  return picks.map((i) => ({ i, left: (center(i) / width.value) * 100, text: props.formatSlot(props.slots[i]!) }))
})
</script>

<template>
  <div v-if="!asTable">
    <div class="flex gap-2">
      <div class="relative w-14 shrink-0 text-right text-caption text-fg-3" :style="{ height: `${H}px` }" aria-hidden="true">
        <span v-for="t in axis.ticks" :key="t" class="tabular absolute right-0 -translate-y-1/2 whitespace-nowrap" :style="{ top: `${y(t)}px` }">{{ tick(t) }}</span>
      </div>
      <!-- Pointer-only tooltip; the same values are in the text summary and the table view, which keyboard and screen reader users get. -->
      <!-- eslint-disable-next-line vuejs-accessibility/no-static-element-interactions, vuejs-accessibility/mouse-events-have-key-events -->
      <div ref="plot" class="relative min-w-0 flex-1" :style="{ height: `${H}px` }" @mousemove="onMove" @mouseleave="hover = null">
        <svg role="img" :aria-label="summary" :viewBox="`0 0 ${width} ${H}`" :width="width" :height="H" class="block overflow-visible">
          <line v-for="t in axis.ticks" :key="t" x1="0" :x2="width" :y1="y(t)" :y2="y(t)" stroke="var(--border)" stroke-width="1" shape-rendering="crispEdges" />
          <rect
            v-if="hover !== null"
            :x="center(hover) - band / 2"
            y="0"
            :width="band"
            :height="H"
            fill="var(--hover)"
          />
          <path v-for="b in bars" :key="b.key" :d="b.d" :fill="b.color" />
          <path
            v-for="s in lines"
            :key="s.id"
            :d="s.d"
            fill="none"
            :stroke="s.color"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          />
          <template v-if="kind === 'line' && hover !== null">
            <template v-for="s in series" :key="s.id">
              <circle v-if="s.values[hover] !== null" :cx="center(hover)" :cy="y(s.values[hover]!)" r="4" :fill="s.color" stroke="var(--surface)" stroke-width="2" />
            </template>
          </template>
        </svg>
        <div
          v-if="hover !== null"
          class="pointer-events-none absolute top-0 z-10 min-w-44 rounded-control bg-overlay p-2 text-small shadow-2"
          :style="hoverLeft > 55 ? { right: `${100 - hoverLeft}%`, marginRight: '12px' } : { left: `${hoverLeft}%`, marginLeft: '12px' }"
          aria-hidden="true"
        >
          <p class="mb-1 font-medium">{{ formatSlot(slots[hover]!) }}</p>
          <p v-for="row in hoverRows" :key="row.id" class="flex items-center justify-between gap-3 text-fg-2">
            <span class="inline-flex min-w-0 items-center gap-1.5"><span class="inline-block size-2 shrink-0 rounded-full" :style="{ background: row.color }" /><span class="truncate">{{ row.name }}</span></span>
            <span class="tabular text-fg">{{ row.value === null ? '—' : format(row.value) }}</span>
          </p>
          <p v-if="stacked && kind === 'bar' && series.length > 1" class="mt-1 flex justify-between gap-3 border-t border-border pt-1 font-medium">
            <span>Total</span><span class="tabular">{{ format(slotTotals[hover]!) }}</span>
          </p>
        </div>
      </div>
    </div>
    <div class="relative ml-16 mt-1 h-4 text-caption text-fg-3" aria-hidden="true">
      <span
        v-for="(l, n) in xLabels"
        :key="l.i"
        class="absolute whitespace-nowrap"
        :class="n === 0 && xLabels.length > 1 ? '' : n === xLabels.length - 1 && xLabels.length > 1 ? '-translate-x-full' : '-translate-x-1/2'"
        :style="{ left: n === 0 && xLabels.length > 1 ? '0' : n === xLabels.length - 1 && xLabels.length > 1 ? '100%' : `${l.left}%` }"
      >{{ l.text }}</span>
    </div>
  </div>
  <div v-else class="max-h-64 overflow-auto">
    <table class="w-full text-small">
      <caption class="sr-only">{{ label }}</caption>
      <thead class="sticky top-0 bg-surface text-caption text-fg-3">
        <tr>
          <th scope="col" class="py-1 pr-3 text-left font-medium">Period</th>
          <th v-for="s in series" :key="s.id" scope="col" class="py-1 pl-3 text-right font-medium">{{ s.name }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(slot, i) in slots" :key="slot" class="border-t border-border">
          <th scope="row" class="whitespace-nowrap py-1 pr-3 text-left font-normal">{{ formatSlot(slot) }}</th>
          <td v-for="s in series" :key="s.id" class="tabular py-1 pl-3 text-right">{{ s.values[i] === null ? '—' : format(s.values[i]!) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
