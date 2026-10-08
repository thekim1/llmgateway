<script setup lang="ts">
import { problemMessage } from '@/utils/problem'
import AppIcon from './AppIcon.vue'
import UiButton from './UiButton.vue'

/** Loading / error / empty wrapper for data-driven sections. */
withDefaults(defineProps<{ loading?: boolean; error?: unknown; empty?: boolean; emptyText?: string; busyLabel?: string }>(), {
  busyLabel: 'Loading',
})
defineEmits<{ (e: 'retry'): void }>()
</script>

<template>
  <div v-if="loading && empty" class="flex items-center gap-2 py-10 text-fg-2" role="status"><AppIcon name="progress_activity" class="animate-spin" />{{ busyLabel }}…</div>
  <div v-else-if="error" role="alert" class="flex flex-col items-start gap-3 rounded-card bg-danger-soft p-5 text-danger">
    <p class="flex items-center gap-2 font-medium"><AppIcon name="error" filled />{{ problemMessage(error) }}</p>
    <UiButton size="sm" @click="$emit('retry')">Try again</UiButton>
  </div>
  <div v-else-if="empty" class="py-10 text-center text-fg-2">{{ emptyText ?? 'Nothing to show yet.' }}</div>
  <slot v-else />
</template>
