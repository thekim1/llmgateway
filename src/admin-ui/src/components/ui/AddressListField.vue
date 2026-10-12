<script setup lang="ts">
import FormField from './FormField.vue'

/** Multi-line text field for IP addresses and CIDR networks, one per line. */
defineProps<{ label: string; modelValue: string; id?: string; hint?: string; error?: string | null; placeholder?: string; rows?: number }>()
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <FormField :id="id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :hint="hint" :error="error">
    <textarea
      :id="fieldId"
      :name="fieldId"
      :value="modelValue"
      :rows="rows ?? 3"
      :placeholder="placeholder"
      spellcheck="false"
      autocapitalize="off"
      autocomplete="off"
      :aria-invalid="invalid ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="field-input !h-auto min-h-[88px] resize-y py-2 font-mono text-small"
      @input="$emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
    />
  </FormField>
</template>
