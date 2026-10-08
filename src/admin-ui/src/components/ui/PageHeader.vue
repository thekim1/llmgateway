<script setup lang="ts">
defineProps<{ title: string; description?: string }>()
</script>

<template>
  <header class="mb-8 flex flex-wrap items-end justify-between gap-6 max-md:mb-6">
    <div class="min-w-0">
      <h1 id="page-title" tabindex="-1" class="text-title-1 outline-none max-md:text-title-2">{{ title }}</h1>
      <p v-if="description || $slots.description" class="mt-1 text-fg-2"><slot name="description">{{ description }}</slot></p>
    </div>
    <div v-if="$slots.default" class="flex flex-wrap items-center gap-2"><slot /></div>
  </header>
</template>
