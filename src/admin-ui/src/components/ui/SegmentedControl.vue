<script setup lang="ts" generic="T extends string | number | boolean">
export interface SegmentOption<V> {
  value: V
  label: string
  count?: number
}

const props = withDefaults(
  defineProps<{
    options: SegmentOption<T>[]
    modelValue: T
    label: string
    /** `filter` uses aria-pressed buttons; `radio` uses a radiogroup for form values. */
    mode?: 'filter' | 'radio'
    full?: boolean
  }>(),
  { mode: 'filter' },
)
const emit = defineEmits<{ (e: 'update:modelValue', value: T): void }>()

function onKey(event: KeyboardEvent, index: number): void {
  if (props.mode !== 'radio') return
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
  if (!step) return
  event.preventDefault()
  const next = props.options[(index + step + props.options.length) % props.options.length]
  if (next) {
    emit('update:modelValue', next.value)
    const group = (event.currentTarget as HTMLElement).parentElement
    requestAnimationFrame(() => (group?.querySelectorAll<HTMLElement>('[role=radio]')[(index + step + props.options.length) % props.options.length])?.focus())
  }
}
</script>

<template>
  <div :role="mode === 'radio' ? 'radiogroup' : 'group'" :aria-label="label" class="flex gap-0.5 rounded-control bg-sunken p-[3px]" :class="full ? 'w-full' : 'w-fit max-w-full flex-wrap'">
    <button
      v-for="(option, index) in options"
      :key="String(option.value)"
      type="button"
      :role="mode === 'radio' ? 'radio' : undefined"
      :aria-pressed="mode === 'filter' ? modelValue === option.value : undefined"
      :aria-checked="mode === 'radio' ? modelValue === option.value : undefined"
      :tabindex="mode === 'radio' && modelValue !== option.value ? -1 : undefined"
      class="inline-flex h-[30px] items-center justify-center gap-1.5 whitespace-nowrap rounded-chip px-3 text-small font-medium"
      :class="[modelValue === option.value ? 'bg-surface text-fg shadow-1' : 'text-fg-2 hover:text-fg', full ? 'flex-1' : '']"
      @click="emit('update:modelValue', option.value)"
      @keydown="onKey($event, index)"
    >
      {{ option.label }}<span v-if="option.count !== undefined" class="tabular text-fg-3">{{ option.count }}</span>
    </button>
  </div>
</template>
