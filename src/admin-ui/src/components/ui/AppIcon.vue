<script setup lang="ts">
/** Material Symbols Rounded, decorative by default. FILL 1 only for active nav and status icons. */
defineProps<{ name: string; filled?: boolean; size?: number }>()
</script>

<template>
  <span class="icon" :class="{ 'icon-filled': filled }" :style="size ? { fontSize: `${size}px` } : undefined" aria-hidden="true">{{ name }}</span>
</template>
