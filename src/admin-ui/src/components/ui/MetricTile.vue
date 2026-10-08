<script setup lang="ts">
import UiMeter from './UiMeter.vue'

defineProps<{
  label: string
  value: string
  context?: string
  /** Optional budget meter in percent. */
  meter?: number | null
  meterLabel?: string
}>()
</script>

<template>
  <div class="material flex flex-col gap-1 rounded-card p-5">
    <span class="text-small font-medium text-fg-2">{{ label }}</span>
    <span class="tabular text-metric">{{ value }}</span>
    <UiMeter v-if="meter != null" :value="meter" :label="meterLabel ?? `${label} used`" thin class="my-1" />
    <span v-if="context" class="text-small text-fg-3">{{ context }}</span>
  </div>
</template>
