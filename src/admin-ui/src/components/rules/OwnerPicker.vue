<script setup lang="ts">
import { computed, ref } from 'vue'
import type { OwnerOption } from '@/composables/useRuleOwners'
import AppIcon from '../ui/AppIcon.vue'

/**
 * Picks several keys, teams or departments for one rule: a filter box and a list of checkboxes. Selected ids that are not
 * among the options (an owner that was removed) stay listed, checked, so saving never drops them silently.
 */
const props = defineProps<{
  /** Id of the filter box; the form focuses it for an error. */
  id: string
  /** Plural noun, e.g. "Teams". */
  label: string
  options: OwnerOption[]
  modelValue: string[]
  error?: string | null
  errorLang?: string
  hint?: string
}>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string[]): void }>()

const query = ref('')
const selected = computed(() => new Set(props.modelValue))
const removed = computed(() => props.modelValue.filter((id) => !props.options.some((o) => o.value === id)))
const visible = computed(() => {
  const q = query.value.trim().toLowerCase()
  const matches = q ? props.options.filter((o) => o.label.toLowerCase().includes(q)) : props.options
  // Selected owners first, so the current choice is visible without scrolling.
  return [...matches].sort((a, b) => Number(selected.value.has(b.value)) - Number(selected.value.has(a.value)))
})
const describedBy = computed(() => [props.error ? `${props.id}-err` : null, props.hint ? `${props.id}-hint` : null, `${props.id}-count`].filter(Boolean).join(' '))

function toggle(value: string, on: boolean): void {
  emit('update:modelValue', on ? [...props.modelValue, value] : props.modelValue.filter((v) => v !== value))
}
</script>

<template>
  <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0" :aria-describedby="describedBy">
    <legend class="mb-1.5 p-0 font-medium">{{ label }}</legend>
    <label :for="id" class="sr-only">Filter {{ label.toLowerCase() }}</label>
    <input
      :id="id"
      v-model="query"
      type="search"
      class="field-input"
      :placeholder="`Filter ${label.toLowerCase()}`"
      autocomplete="off"
      :aria-invalid="error ? 'true' : undefined"
      :aria-describedby="describedBy"
    />
    <ul class="m-0 flex max-h-64 list-none flex-col gap-0.5 overflow-y-auto rounded-tile bg-surface-2 p-2" :aria-label="`${label} to choose from`">
      <li v-for="owner in removed" :key="owner">
        <label class="flex cursor-pointer items-center gap-3 rounded-control px-2 py-1.5 hover:bg-hover">
          <input type="checkbox" checked @change="toggle(owner, ($event.target as HTMLInputElement).checked)" />
          <span class="text-fg-2">Removed <span class="font-mono text-small">{{ owner.slice(0, 8) }}</span> (no longer exists)</span>
        </label>
      </li>
      <li v-for="option in visible" :key="option.value">
        <label class="flex cursor-pointer items-center gap-3 rounded-control px-2 py-1.5 hover:bg-hover">
          <input type="checkbox" :checked="selected.has(option.value)" @change="toggle(option.value, ($event.target as HTMLInputElement).checked)" />
          <span lang="sv" class="break-words">{{ option.label }}</span>
        </label>
      </li>
      <li v-if="visible.length === 0 && removed.length === 0" class="px-2 py-1.5 text-fg-3">{{ query ? 'Nothing matches the filter.' : 'Nothing to choose from yet.' }}</li>
    </ul>
    <span :id="`${id}-count`" class="text-small text-fg-3">{{ modelValue.length }} selected</span>
    <span v-if="error" :id="`${id}-err`" :lang="errorLang" class="flex items-start gap-1 text-small text-danger"><AppIcon name="error" :size="16" filled class="mt-px" />{{ error }}</span>
    <span v-if="hint" :id="`${id}-hint`" class="text-small text-fg-3">{{ hint }}</span>
  </fieldset>
</template>
