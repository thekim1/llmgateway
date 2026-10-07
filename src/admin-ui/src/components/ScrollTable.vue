<script setup lang="ts">
/**
 * Horizontally scrollable, keyboard-focusable region for wide tables (WCAG 1.4.10 reflow).
 * The table must have a caption whose id is passed as `labelledby`.
 */
defineProps<{ labelledby?: string; label?: string }>()
</script>

<template>
  <!-- eslint-disable-next-line vuejs-accessibility/no-noninteractive-tabindex -- scrollable region must be keyboard reachable -->
  <div class="table-scroll" role="region" :aria-labelledby="labelledby" :aria-label="label" tabindex="0">
    <slot />
  </div>
</template>
