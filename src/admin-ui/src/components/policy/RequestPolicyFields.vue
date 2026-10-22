<script setup lang="ts">
import type { PolicyDraft, PolicyErrors } from './policyDraft'
import FieldGroup from '../ui/FieldGroup.vue'
import TextAreaField from '../ui/TextAreaField.vue'
import TextField from '../ui/TextField.vue'

/** The request policy part of the team and key forms. */
defineProps<{ idPrefix: string; errors: PolicyErrors; hint: string }>()
const draft = defineModel<PolicyDraft>({ required: true })

function set(key: keyof PolicyDraft, value: string): void {
  draft.value = { ...draft.value, [key]: value }
}
</script>

<template>
  <FieldGroup legend="Request policy" :hint="hint">
    <TextField
      :id="`${idPrefix}-max-output`"
      :model-value="draft.maxOutputTokens"
      label="Max output tokens"
      inputmode="numeric"
      hint="Requests asking for more are lowered to this; requests that do not say get it."
      :error="errors.maxOutputTokens"
      @update:model-value="set('maxOutputTokens', $event)"
    />
    <TextAreaField
      :id="`${idPrefix}-system-prompt`"
      :model-value="draft.systemPrompt"
      label="System prompt"
      :rows="4"
      hint="Placed before the application's own instructions in chat, Responses and Messages requests."
      :error="errors.systemPrompt"
      @update:model-value="set('systemPrompt', $event)"
    />
    <TextAreaField
      :id="`${idPrefix}-defaults`"
      :model-value="draft.defaults"
      label="Default parameters"
      mono
      placeholder="{&quot;temperature&quot;: 0.2}"
      hint="JSON. Used when a request leaves the parameter out."
      :error="errors.defaults"
      @update:model-value="set('defaults', $event)"
    />
    <TextAreaField
      :id="`${idPrefix}-overrides`"
      :model-value="draft.overrides"
      label="Forced parameters"
      mono
      placeholder="{&quot;reasoning_effort&quot;: &quot;low&quot;}"
      hint="JSON. Always used, whatever the request says. The model, the messages and streaming cannot be set."
      :error="errors.overrides"
      @update:model-value="set('overrides', $event)"
    />
  </FieldGroup>
</template>
