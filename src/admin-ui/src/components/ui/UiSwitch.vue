<script setup lang="ts">
/**
 * An on/off switch: a button with role="switch" and aria-checked, so Space and Enter toggle it and screen readers announce
 * "on" or "off". The accessible name comes from `label` (visually hidden) or from `labelledby`. It does not change itself:
 * the parent decides (for example after the server agreed), so a rejected change simply leaves it where it was. While
 * `busy` it ignores clicks but stays focusable, so keyboard focus is not lost.
 */
withDefaults(defineProps<{ modelValue: boolean; label?: string; labelledby?: string; describedby?: string; disabled?: boolean; busy?: boolean }>(), {
  label: undefined,
  labelledby: undefined,
  describedby: undefined,
})
defineEmits<{ (e: 'update:modelValue', value: boolean): void }>()
</script>

<template>
  <button
    type="button"
    role="switch"
    :aria-checked="modelValue ? 'true' : 'false'"
    :aria-label="labelledby ? undefined : label"
    :aria-labelledby="labelledby"
    :aria-describedby="describedby"
    :aria-busy="busy ? 'true' : undefined"
    :disabled="disabled"
    class="relative inline-flex h-6 w-11 flex-none items-center rounded-full border-2 border-transparent transition-colors motion-reduce:transition-none disabled:cursor-not-allowed disabled:opacity-50 forced-colors:border-[ButtonText] forced-colors:aria-checked:bg-[Highlight]"
    :class="modelValue ? 'bg-accent' : 'bg-fg-3'"
    @click="!busy && $emit('update:modelValue', !modelValue)"
  >
    <span
      aria-hidden="true"
      class="inline-block size-5 rounded-full bg-surface shadow-1 transition-transform motion-reduce:transition-none forced-colors:bg-[ButtonText]"
      :class="modelValue ? 'translate-x-5' : 'translate-x-0'"
    />
  </button>
</template>

