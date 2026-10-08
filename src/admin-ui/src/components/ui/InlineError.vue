<script setup lang="ts">
import AppIcon from './AppIcon.vue'

withDefaults(defineProps<{ message: string; lang?: string; title?: string; tone?: 'danger' | 'warn' }>(), { tone: 'danger' })
</script>

<template>
  <div
    role="alert"
    class="flex items-start gap-2 rounded-control p-3 text-small"
    :class="tone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-warn-soft text-warn'"
  >
    <AppIcon :name="tone === 'danger' ? 'error' : 'warning'" :size="18" filled class="mt-px" />
    <div class="min-w-0">
      <p v-if="title" class="font-medium">{{ title }}</p>
      <p :lang="lang">{{ message }}</p>
      <slot />
    </div>
  </div>
</template>
