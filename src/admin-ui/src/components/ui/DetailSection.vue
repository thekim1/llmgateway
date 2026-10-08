<script setup lang="ts">
/** A titled `dl` section for drawers: caption heading, rows of label / value. */
defineProps<{ title: string }>()
</script>

<template>
  <section class="mb-6">
    <h3 class="mb-1 text-caption text-fg-3">{{ title }}</h3>
    <dl class="m-0"><slot /></dl>
  </section>
</template>
