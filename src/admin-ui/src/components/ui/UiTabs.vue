<script setup lang="ts" generic="T extends string">
export interface TabOption<V> {
  value: V
  label: string
  icon?: string
}

/**
 * `id` prefixes the ids: tab `<id>-tab-<value>` controls panel `<id>-panel-<value>`, which the page renders with
 * role="tabpanel" and aria-labelledby pointing back at the tab.
 */
const props = defineProps<{ id: string; tabs: TabOption<T>[]; modelValue: T; label: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: T): void }>()

// Automatic activation (WAI-ARIA tabs pattern): arrow keys, Home and End move and select.
function onKey(event: KeyboardEvent, index: number): void {
  const count = props.tabs.length
  const next =
    event.key === 'ArrowRight' ? (index + 1) % count
    : event.key === 'ArrowLeft' ? (index - 1 + count) % count
    : event.key === 'Home' ? 0
    : event.key === 'End' ? count - 1
    : -1
  if (next < 0) return
  event.preventDefault()
  emit('update:modelValue', props.tabs[next]!.value)
  const list = (event.currentTarget as HTMLElement).parentElement
  requestAnimationFrame(() => list?.querySelectorAll<HTMLElement>('[role=tab]')[next]?.focus())
}
</script>

<template>
  <div role="tablist" :aria-label="label" class="flex gap-1 border-b border-border">
    <button
      v-for="(tab, index) in tabs"
      :id="`${id}-tab-${tab.value}`"
      :key="tab.value"
      type="button"
      role="tab"
      :aria-selected="modelValue === tab.value"
      :aria-controls="`${id}-panel-${tab.value}`"
      :tabindex="modelValue === tab.value ? 0 : -1"
      class="-mb-px inline-flex h-10 items-center gap-1.5 border-b-2 px-3 text-body font-medium"
      :class="modelValue === tab.value ? 'border-accent text-fg' : 'border-transparent text-fg-2 hover:text-fg'"
      @click="emit('update:modelValue', tab.value)"
      @keydown="onKey($event, index)"
    >
      <span v-if="tab.icon" class="icon text-[18px]" aria-hidden="true">{{ tab.icon }}</span>{{ tab.label }}
    </button>
  </div>
</template>
