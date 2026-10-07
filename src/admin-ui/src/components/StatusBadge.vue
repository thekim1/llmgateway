<script setup lang="ts">
import AppIcon from '@/components/AppIcon.vue'
import type { StatusTone } from '@/components/types'

/** Status shown as icon + text (never colour alone). */
defineProps<{
  tone: StatusTone
  icon: string
  text: string
}>()
</script>

<template>
  <span class="badge" :class="`badge--${tone}`">
    <AppIcon :name="icon" />
    <span>{{ text }}</span>
  </span>
</template>
