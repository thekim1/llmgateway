<script setup lang="ts">
import FormField from './FormField.vue'

const props = withDefaults(
  defineProps<{
    label: string
    modelValue: string
    id?: string
    type?: string
    hint?: string
    error?: string | null
    placeholder?: string
    inputmode?: 'text' | 'numeric' | 'decimal' | 'url' | 'email'
    mono?: boolean
    disabled?: boolean
    autocomplete?: string
    name?: string
    min?: string | number
    max?: string | number
  }>(),
  { type: 'text' },
)
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <FormField :id="props.id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :hint="hint" :error="error">
    <input
      :id="fieldId"
      :name="name ?? fieldId"
      :value="modelValue"
      :type="type"
      :inputmode="inputmode"
      :placeholder="placeholder"
      :disabled="disabled"
      :autocomplete="autocomplete ?? 'off'"
      :min="min"
      :max="max"
      :aria-invalid="invalid ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="field-input"
      :class="{ 'font-mono': mono }"
      @input="$emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />
  </FormField>
</template>
