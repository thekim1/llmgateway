<script setup lang="ts">
import { useId } from 'vue'

/** Fieldset with a visible legend, used to group related form controls. */
defineProps<{ legend: string; hint?: string }>()
const id = useId()
</script>

<template>
  <fieldset class="m-0 flex min-w-0 flex-col gap-4 border-0 p-0" :aria-describedby="hint ? `fs-${id}` : undefined">
    <legend class="mb-3 p-0 text-heading">{{ legend }}</legend>
    <p v-if="hint" :id="`fs-${id}`" class="-mt-2 text-small text-fg-3">{{ hint }}</p>
    <slot />
  </fieldset>
</template>
