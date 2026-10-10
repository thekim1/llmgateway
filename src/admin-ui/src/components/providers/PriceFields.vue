<script setup lang="ts">
import type { PriceDraft, PriceErrors } from './priceDraft'
import TextField from '../ui/TextField.vue'

defineProps<{ errors: PriceErrors; idPrefix: string; optionalFrom?: boolean }>()
const draft = defineModel<PriceDraft>({ required: true })

function set(key: keyof PriceDraft, value: string): void {
  draft.value = { ...draft.value, [key]: value }
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <TextField :id="`${idPrefix}-input`" :model-value="draft.input" label="Input (USD per 1M tokens)" inputmode="decimal" :error="errors.input" @update:model-value="set('input', $event)" />
    <TextField :id="`${idPrefix}-cached`" :model-value="draft.cached" label="Cached input (USD per 1M tokens)" inputmode="decimal" :error="errors.cached" @update:model-value="set('cached', $event)" />
    <TextField :id="`${idPrefix}-output`" :model-value="draft.output" label="Output (USD per 1M tokens)" inputmode="decimal" :error="errors.output" @update:model-value="set('output', $event)" />
    <TextField
      :id="`${idPrefix}-audio`"
      :model-value="draft.audio"
      label="Audio (USD per minute)"
      inputmode="decimal"
      hint="Optional. For speech-to-text models billed by duration, such as Whisper. Leave the token prices at 0 for those."
      :error="errors.audio"
      @update:model-value="set('audio', $event)"
    />
    <TextField
      :id="`${idPrefix}-audio-in`"
      :model-value="draft.audioIn"
      label="Audio input (USD per 1M tokens)"
      inputmode="decimal"
      hint="Optional. For models that price audio tokens above text tokens, such as gpt-realtime and gpt-4o-transcribe. Empty bills audio tokens at the input price."
      :error="errors.audioIn"
      @update:model-value="set('audioIn', $event)"
    />
    <TextField
      :id="`${idPrefix}-audio-out`"
      :model-value="draft.audioOut"
      label="Audio output (USD per 1M tokens)"
      inputmode="decimal"
      hint="Optional. Spoken output of realtime models. Empty bills it at the output price."
      :error="errors.audioOut"
      @update:model-value="set('audioOut', $event)"
    />
    <TextField
      v-if="optionalFrom"
      :id="`${idPrefix}-from`"
      :model-value="draft.from"
      label="Effective from"
      type="datetime-local"
      hint="Optional. Defaults to now."
      :error="errors.from"
      @update:model-value="set('from', $event)"
    />
  </div>
</template>
