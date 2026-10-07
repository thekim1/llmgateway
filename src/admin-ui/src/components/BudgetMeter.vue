<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import { clampPercent, formatPercent, formatSek } from '@/utils/format'

/** Budget usage as role="meter" with a text equivalent "x av y kr (z %)". */
const props = withDefaults(
  defineProps<{
    label: string
    spent: number
    limit: number
    /** Percent used (0–100). Computed from spent/limit if omitted. */
    percent?: number
    thresholds?: number[]
  }>(),
  { percent: undefined, thresholds: () => [] },
)

const { t } = useI18n()

const pct = computed(() => props.limit <= 0 ? 100 : props.percent ?? (props.spent / props.limit) * 100)
const level = computed<'ok' | 'warning' | 'danger'>(() => {
  if (pct.value >= 100) return 'danger'
  const lowest = props.thresholds.length ? Math.min(...props.thresholds) : 80
  return pct.value >= lowest ? 'warning' : 'ok'
})
const text = computed(() =>
  t('budgets.meterText', {
    spent: formatSek(props.spent),
    limit: formatSek(props.limit),
    percent: formatPercent(pct.value),
  }),
)
const icon = computed(() => ({ ok: 'check_circle', warning: 'warning', danger: 'error' })[level.value])
</script>

<template>
  <div class="meter-wrap" :class="`meter-wrap--${level}`">
    <div
      class="meter"
      role="meter"
      :aria-label="label"
      aria-valuemin="0"
      aria-valuemax="100"
      :aria-valuenow="clampPercent(pct)"
      :aria-valuetext="text"
    >
      <span class="meter__fill" :style="{ width: `${clampPercent(pct)}%` }" />
    </div>
    <p class="meter__text">
      <AppIcon :name="icon" />
      <span>{{ text }}</span>
      <span v-if="level !== 'ok'" class="meter__level">– {{ t(`budgets.level.${level}`) }}</span>
    </p>
  </div>
</template>
