<script setup lang="ts">
defineProps<{ label: string }>()
</script>

<template>
  <div class="grid grid-cols-[150px_1fr] gap-3 border-b border-border py-2.5 max-md:grid-cols-1 max-md:gap-0.5">
    <dt class="text-fg-2">{{ label }}</dt>
    <dd class="m-0 min-w-0 break-words"><slot /></dd>
  </div>
</template>
