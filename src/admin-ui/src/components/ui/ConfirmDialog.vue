<script setup lang="ts">
import { ref, watch } from 'vue'
import type { Tone } from '@/utils/labels'
import UiButton from './UiButton.vue'
import UiDialog from './UiDialog.vue'
import InlineError from './InlineError.vue'
import { problemLang, problemMessage } from '@/utils/problem'

const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    /** Say exactly what happens, e.g. "Requests fail with key_revoked." */
    consequence?: string
    confirmLabel: string
    cancelLabel?: string
    danger?: boolean
    icon?: string
    tone?: Tone
    /** Async action; the dialog closes when it resolves and shows the error inline when it rejects. */
    action?: () => Promise<unknown> | unknown
  }>(),
  { cancelLabel: 'Cancel', danger: false },
)
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'confirm'): void }>()

const busy = ref(false)
const error = ref<unknown>(null)

watch(
  () => props.open,
  (open) => {
    if (open) error.value = null
  },
)

async function confirm(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await props.action?.()
    emit('confirm')
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog
    :open="open"
    :title="title"
    :icon="icon ?? (danger ? 'block' : undefined)"
    :tone="tone ?? (danger ? 'danger' : 'neutral')"
    @update:open="emit('update:open', $event)"
  >
    <template v-if="consequence || $slots.default" #description><slot>{{ consequence }}</slot></template>
    <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
    <template #actions>
      <UiButton @click="emit('update:open', false)">{{ cancelLabel }}</UiButton>
      <UiButton :variant="danger ? 'danger-solid' : 'primary'" :loading="busy" @click="confirm">{{ confirmLabel }}</UiButton>
    </template>
  </UiDialog>
</template>
