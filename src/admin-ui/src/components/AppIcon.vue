<script setup lang="ts">
/**
 * Material Symbols Sharp icon. Decorative by default (aria-hidden); pass `label` when the icon
 * is the only content and must be announced.
 */
defineProps<{
  name: string
  label?: string
  filled?: boolean
}>()
</script>

<template>
  <span
    v-if="label"
    class="material-symbols-sharp app-icon"
    :class="{ 'app-icon--filled': filled }"
    role="img"
    :aria-label="label"
    >{{ name }}</span>
  <span
    v-else
    class="material-symbols-sharp app-icon"
    :class="{ 'app-icon--filled': filled }"
    aria-hidden="true"
    >{{ name }}</span>
</template>
