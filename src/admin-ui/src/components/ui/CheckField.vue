<script setup lang="ts">
import { useId } from 'vue'

defineProps<{ label: string; description?: string; modelValue: boolean; disabled?: boolean }>()
defineEmits<{ (e: 'update:modelValue', value: boolean): void }>()
const id = useId()
</script>

<template>
  <label :for="`c-${id}`" class="flex cursor-pointer items-start gap-3 rounded-control p-1">
    <input
      :id="`c-${id}`"
      type="checkbox"
      class="mt-px"
      :checked="modelValue"
      :disabled="disabled"
      @change="$emit('update:modelValue', ($event.target as HTMLInputElement).checked)"
    />
    <span class="flex flex-col">
      <span class="font-medium">{{ label }}</span>
      <span v-if="description" class="text-small text-fg-3">{{ description }}</span>
    </span>
  </label>
</template>
