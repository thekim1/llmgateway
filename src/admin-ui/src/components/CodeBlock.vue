<script setup lang="ts">
import CopyButton from '@/components/CopyButton.vue'

/** Read-only code snippet with a copy button. The scrollable block is keyboard focusable. */
defineProps<{ code: string; label: string; language?: string }>()
const id = `code-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <div class="code-block">
    <div class="code-block__toolbar">
      <span :id="`${id}-label`" class="code-block__label">{{ label }}</span>
      <CopyButton :text="code" :described-by="`${id}-label`" />
    </div>
    <!-- eslint-disable-next-line vuejs-accessibility/no-noninteractive-tabindex -- scrollable code must be keyboard reachable -->
    <pre class="code-block__pre" tabindex="0" role="region" :aria-labelledby="`${id}-label`"><code :class="language ? `language-${language}` : undefined">{{ code }}</code></pre>
  </div>
</template>
