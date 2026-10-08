<script setup lang="ts">
import { TONE_CLASSES, type Tone } from '@/utils/labels'

withDefaults(defineProps<{ tone?: Tone; label?: string; dot?: boolean }>(), { tone: 'neutral', dot: true })
</script>

<template>
  <span class="badge" :class="TONE_CLASSES[tone]">
    <span v-if="dot" class="size-1.5 rounded-full bg-current" aria-hidden="true" />
    <slot>{{ label }}</slot>
  </span>
</template>
