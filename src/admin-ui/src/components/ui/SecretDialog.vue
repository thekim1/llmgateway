<script setup lang="ts">
import { ref, watch } from 'vue'
import { useCopy } from '@/composables/useCopy'
import AppIcon from './AppIcon.vue'
import CheckField from './CheckField.vue'
import UiButton from './UiButton.vue'
import UiDialog from './UiDialog.vue'

const props = defineProps<{ open: boolean; secret: string }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'done'): void }>()

const stored = ref(false)
const { copied, copy } = useCopy()

watch(
  () => props.open,
  (open) => {
    if (open) stored.value = false
  },
)

function done(): void {
  if (!stored.value) return
  emit('done')
  emit('update:open', false)
}

function onOpenChange(value: boolean): void {
  if (!value && !stored.value) return
  emit('update:open', value)
}
</script>

<template>
  <UiDialog
    :open="open"
    title="Copy your secret now"
    description="This is the only time it's shown. If you lose it, rotate the key."
    icon="key"
    tone="accent"
    :dismissible="stored"
    @update:open="onOpenChange"
  >
    <div class="flex items-center gap-2 rounded-control bg-sunken p-3">
      <code class="min-w-0 flex-1 break-all font-mono text-small" data-testid="secret">{{ secret }}</code>
      <UiButton size="sm" :icon="copied ? 'check' : 'content_copy'" @click="copy(secret)">{{ copied ? 'Copied' : 'Copy' }}</UiButton>
    </div>
    <span class="sr-only" role="status">{{ copied ? 'Secret copied to clipboard' : '' }}</span>
    <CheckField v-model="stored" label="I've stored the secret somewhere safe" />
    <p v-if="!stored" class="-mt-2 flex items-center gap-1 text-small text-fg-3"><AppIcon name="info" :size="16" />Tick the box to enable Done.</p>
    <template #actions>
      <UiButton variant="primary" :disabled="!stored" @click="done">Done</UiButton>
    </template>
  </UiDialog>
</template>
