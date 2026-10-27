<script setup lang="ts">
import { AUDIO_UNIT_LABEL, type AudioUnit, type PriceDraft, type PriceErrors } from './priceDraft'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'

defineProps<{ errors: PriceErrors; idPrefix: string; optionalFrom?: boolean }>()
const draft = defineModel<PriceDraft>({ required: true })

const audioUnits = (Object.keys(AUDIO_UNIT_LABEL) as AudioUnit[]).map((value) => ({ value, label: AUDIO_UNIT_LABEL[value] }))

function set(key: keyof PriceDraft, value: string): void {
  draft.value = { ...draft.value, [key]: value }
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <TextField :id="`${idPrefix}-input`" :model-value="draft.input" label="Input (USD per 1M tokens)" inputmode="decimal" :error="errors.input" @update:model-value="set('input', $event)" />
    <TextField :id="`${idPrefix}-cached`" :model-value="draft.cached" label="Cached input (USD per 1M tokens)" inputmode="decimal" :error="errors.cached" @update:model-value="set('cached', $event)" />
    <TextField
      :id="`${idPrefix}-cache-write`"
      :model-value="draft.cacheWrite"
      label="Cache write (USD per 1M tokens)"
      inputmode="decimal"
      hint="Optional. For providers that charge for writing the prompt cache, such as Anthropic (1.25 times the input price, 2 times for the 1-hour cache). Empty bills cache writes at the input price."
      :error="errors.cacheWrite"
      @update:model-value="set('cacheWrite', $event)"
    />
    <TextField :id="`${idPrefix}-output`" :model-value="draft.output" label="Output (USD per 1M tokens)" inputmode="decimal" :error="errors.output" @update:model-value="set('output', $event)" />
    <div class="flex items-start gap-3">
      <TextField
        :id="`${idPrefix}-audio`"
        class="min-w-0 flex-1"
        :model-value="draft.audio"
        label="Audio input by duration (USD)"
        inputmode="decimal"
        hint="Optional. For models billed by the length of the input audio, such as Whisper per minute or a server billed per hour. The audio's tokens are then not billed as text; set an audio input token price as well only if the provider charges both."
        :error="errors.audio"
        @update:model-value="set('audio', $event)"
      />
      <SelectField :id="`${idPrefix}-audio-unit`" class="w-36 shrink-0" :model-value="draft.audioUnit" label="Unit" :options="audioUnits" @update:model-value="set('audioUnit', $event)" />
    </div>
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
      :id="`${idPrefix}-image`"
      :model-value="draft.image"
      label="Image (USD per picture)"
      inputmode="decimal"
      hint="Optional. For image models billed per picture, such as DALL·E. Token-billed models such as gpt-image-1 use the token prices; leave this empty for them."
      :error="errors.image"
      @update:model-value="set('image', $event)"
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
