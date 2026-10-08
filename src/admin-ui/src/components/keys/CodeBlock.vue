<script setup lang="ts">
import { useCopy } from '@/composables/useCopy'
import UiButton from '@/components/ui/UiButton.vue'

defineProps<{ code: string; label: string }>()
const { copied, copy } = useCopy()
</script>

<template>
  <figure class="m-0 rounded-tile bg-sunken">
    <figcaption class="flex items-center justify-between gap-3 px-4 pt-3">
      <span class="text-caption text-fg-3">{{ label }}</span>
      <UiButton size="sm" :icon="copied ? 'check' : 'content_copy'" @click="copy(code)">{{ copied ? 'Copied' : 'Copy' }}</UiButton>
    </figcaption>
    <pre class="m-0 overflow-x-auto px-4 pb-4 pt-2 font-mono text-small" tabindex="0"><code>{{ code }}</code></pre>
    <span class="sr-only" role="status">{{ copied ? `${label} copied to clipboard` : '' }}</span>
  </figure>
</template>
