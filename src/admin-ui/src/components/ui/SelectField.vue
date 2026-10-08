<script setup lang="ts">
import FormField from './FormField.vue'

export interface SelectOption {
  value: string
  label: string
  disabled?: boolean
}

const props = defineProps<{
  id?: string
  label: string
  modelValue: string
  options: SelectOption[]
  hint?: string
  error?: string | null
  disabled?: boolean
  placeholder?: string
}>()
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <FormField :id="props.id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :hint="hint" :error="error">
    <select
      :id="fieldId"
      :value="modelValue"
      :disabled="disabled"
      :aria-invalid="invalid ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="field-input"
      @change="$emit('update:modelValue', ($event.target as HTMLSelectElement).value)"
    >
      <option v-if="placeholder" value="" disabled>{{ placeholder }}</option>
      <option v-for="option in options" :key="option.value" :value="option.value" :disabled="option.disabled">{{ option.label }}</option>
    </select>
  </FormField>
</template>
