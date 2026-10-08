<script setup lang="ts">
import type { DataResidency } from '@/api/types'
import { RESIDENCY } from '@/utils/labels'

defineProps<{ residency: DataResidency }>()
</script>

<template>
  <span class="whitespace-nowrap text-small" :class="RESIDENCY[residency].cls"><span aria-hidden="true">{{ RESIDENCY[residency].glyph }} </span>{{ RESIDENCY[residency].label }}</span>
</template>
