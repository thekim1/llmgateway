<script setup lang="ts">
withDefaults(defineProps<{ title?: string; meta?: string; padding?: 'md' | 'lg' | 'none'; as?: string; headingId?: string }>(), {
  padding: 'md',
  as: 'section',
})
</script>

<template>
  <component :is="as" class="material rounded-card" :class="{ 'p-5': padding === 'md', 'p-6': padding === 'lg' }" :aria-labelledby="title ? headingId : undefined">
    <div v-if="title || $slots.actions" class="mb-4 flex items-center justify-between gap-4" :class="{ 'px-5 pt-5 !mb-3': padding === 'none' }">
      <h2 :id="headingId" class="text-heading">{{ title }}</h2>
      <span v-if="meta" class="text-small text-fg-3">{{ meta }}</span>
      <slot name="actions" />
    </div>
    <slot />
  </component>
</template>
