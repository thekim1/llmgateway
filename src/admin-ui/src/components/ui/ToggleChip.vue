<script setup lang="ts">
import AppIcon from './AppIcon.vue'

defineProps<{ label: string; modelValue: boolean; glyph?: string }>()
defineEmits<{ (e: 'update:modelValue', value: boolean): void }>()
</script>

<template>
  <button
    type="button"
    :aria-pressed="modelValue"
    class="inline-flex h-ctl items-center gap-1.5 rounded-control px-3 font-medium"
    :class="modelValue ? 'bg-accent-soft text-accent-ink shadow-inset-selected' : 'bg-surface text-fg shadow-inset-input'"
    @click="$emit('update:modelValue', !modelValue)"
  >
    <AppIcon v-if="modelValue" name="check_circle" :size="18" filled />
    <span v-else-if="glyph" aria-hidden="true">{{ glyph }}</span>
    {{ label }}
  </button>
</template>
