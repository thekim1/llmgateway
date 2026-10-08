<script setup lang="ts">
import { ref, watch } from 'vue'
import type { KeyRotationMode, RotateKeyResponse, VirtualKey } from '@/api/types'
import { useKeysStore } from '@/stores/keys'
import { problemLang, problemMessage } from '@/utils/problem'
import AppIcon from '@/components/ui/AppIcon.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'

const props = defineProps<{ open: boolean; keyData: VirtualKey | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'rotated', result: RotateKeyResponse): void }>()

const keys = useKeysStore()
const mode = ref<KeyRotationMode>('RevokeImmediately')
const busy = ref(false)
const error = ref<unknown>(null)

const options: { value: KeyRotationMode; icon: string; title: string; text: string }[] = [
  { value: 'RevokeImmediately', icon: 'block', title: 'Revoke old key now', text: 'Applications using the old key fail with key_revoked straight away.' },
  { value: 'Grace24Hours', icon: 'schedule', title: 'Keep old key for 24 hours', text: 'Both keys work for 24 hours so you can switch applications over.' },
]

watch(
  () => props.open,
  (open) => {
    if (open) {
      mode.value = 'RevokeImmediately'
      error.value = null
    }
  },
)

async function rotate(): Promise<void> {
  if (!props.keyData || busy.value) return
  busy.value = true
  error.value = null
  try {
    const result = await keys.rotate(props.keyData.id, mode.value)
    emit('update:open', false)
    emit('rotated', result)
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
    :title="`Rotate ${keyData?.name ?? 'key'}`"
    description="Create a new secret and choose when the old one stops working."
    icon="autorenew"
    tone="accent"
    :dismissible="!busy"
    @update:open="emit('update:open', $event)"
  >
    <form id="rotate-form" class="m-0 flex flex-col gap-3" @submit.prevent="rotate">
      <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend class="mb-2 p-0 font-medium">When should the old key stop working?</legend>
        <label
          v-for="option in options"
          :key="option.value"
          class="flex cursor-pointer items-start gap-3 rounded-tile p-3"
          :class="mode === option.value ? 'bg-accent-soft shadow-inset-selected' : 'bg-surface shadow-inset-input'"
        >
          <input v-model="mode" type="radio" name="rotate-mode" class="mt-1" :value="option.value" />
          <span class="flex min-w-0 flex-col">
            <span class="flex items-center gap-1.5 font-medium"><AppIcon :name="option.icon" :size="18" />{{ option.title }}</span>
            <span class="text-small text-fg-2">{{ option.text }}</span>
          </span>
        </label>
      </fieldset>
      <p class="text-small text-fg-3">Budgets and spend carry over to the new key.</p>
      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
    </form>
    <template #actions>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton variant="primary" type="submit" form="rotate-form" :loading="busy">Rotate key</UiButton>
    </template>
  </UiDialog>
</template>
