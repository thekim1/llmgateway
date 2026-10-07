<script setup lang="ts">
import AppIcon from '@/components/AppIcon.vue'

/** Empty state that explains the next step. Put the primary action in the default slot. */
withDefaults(defineProps<{ icon?: string; title: string; text: string; level?: 2 | 3 }>(), {
  icon: 'inbox',
  level: 2,
})
</script>

<template>
  <div class="empty-state">
    <AppIcon :name="icon" class="empty-state__icon" />
    <component :is="`h${level}`" class="empty-state__title">{{ title }}</component>
    <p>{{ text }}</p>
    <div v-if="$slots.default" class="empty-state__actions">
      <slot />
    </div>
  </div>
</template>
