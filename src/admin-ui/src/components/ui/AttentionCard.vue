<script setup lang="ts">
import { TONE_CLASSES, type Tone } from '@/utils/labels'
import AppIcon from './AppIcon.vue'
import UiButton from './UiButton.vue'

defineProps<{ kind: string; title: string; body: string; icon: string; tone: Tone; action?: string }>()
defineEmits<{ (e: 'action'): void }>()

const KIND_TEXT: Record<Tone, string> = { ok: 'text-ok', warn: 'text-warn', danger: 'text-danger', neutral: 'text-fg-2', accent: 'text-accent-ink' }
</script>

<template>
  <article class="material flex flex-col gap-3 rounded-card p-5">
    <div class="flex items-center gap-3">
      <span class="grid size-8 flex-none place-items-center rounded-control" :class="TONE_CLASSES[tone]"><AppIcon :name="icon" filled /></span>
      <div class="min-w-0">
        <span class="text-caption" :class="KIND_TEXT[tone]">{{ kind }}</span>
        <h3 class="text-heading">{{ title }}</h3>
      </div>
    </div>
    <p class="text-small text-fg-2">{{ body }}</p>
    <div v-if="action"><UiButton size="sm" @click="$emit('action')">{{ action }}</UiButton></div>
  </article>
</template>
