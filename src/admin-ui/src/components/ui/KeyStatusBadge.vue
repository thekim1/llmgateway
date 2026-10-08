<script setup lang="ts">
import type { KeyStatus } from '@/api/types'
import { KEY_STATUS } from '@/utils/labels'
import UiBadge from './UiBadge.vue'

defineProps<{ status: KeyStatus }>()
</script>

<template>
  <UiBadge :tone="KEY_STATUS[status].tone" :label="KEY_STATUS[status].label" />
</template>
