<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppDialog from '@/components/AppDialog.vue'
import AppIcon from '@/components/AppIcon.vue'
import CopyButton from '@/components/CopyButton.vue'

/**
 * Shows a newly created/rotated key secret exactly once. The dialog cannot be closed (button,
 * Escape or outside click) until the user confirms that the key has been saved.
 */
const props = defineProps<{
  open: boolean
  secret: string
  keyName: string
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  /** Emitted after the user has acknowledged and closed – the parent must discard the secret. */
  closed: []
}>()

const { t } = useI18n()
const acknowledged = ref(false)
const error = ref<string | null>(null)

watch(
  () => props.open,
  (open) => {
    if (open) {
      acknowledged.value = false
      error.value = null
    }
  },
)

watch(acknowledged, (value) => {
  if (value) error.value = null
})

async function blocked(): Promise<void> {
  error.value = t('secret.mustAcknowledge')
  await nextTick()
  document.getElementById('secret-ack')?.focus()
}

function close(): void {
  if (!acknowledged.value) {
    void blocked()
    return
  }
  emit('update:open', false)
  emit('closed')
}
</script>

<template>
  <AppDialog :open="open" :title="t('secret.title')" :persistent="!acknowledged" :show-close="false" @blocked-close="blocked">
    <div class="notice notice--warning">
      <AppIcon name="warning" />
      <p>{{ t('secret.warning') }}</p>
    </div>
    <p>{{ t('secret.intro', { name: keyName }) }}</p>
    <p id="secret-label" class="field__label">{{ t('secret.label') }}</p>
    <div class="secret">
      <code class="secret__value" data-testid="secret-value">{{ secret }}</code>
      <CopyButton :text="secret" :label="t('secret.copy')" described-by="secret-label" />
    </div>
    <p class="help">{{ t('secret.storageHint') }}</p>
    <div class="field" :class="{ 'field--invalid': !!error }">
      <p v-if="error" id="secret-ack-error" class="field__error">
        <AppIcon name="error" />
        <span>{{ error }}</span>
      </p>
      <div class="check">
        <input
          id="secret-ack"
          v-model="acknowledged"
          type="checkbox"
          :aria-invalid="!!error || undefined"
          :aria-describedby="error ? 'secret-ack-error' : undefined"
        />
        <label for="secret-ack">{{ t('secret.acknowledge') }}</label>
      </div>
    </div>
    <template #footer>
      <button type="button" class="btn btn--primary" data-testid="secret-close" @click="close">
        {{ t('secret.close') }}
      </button>
    </template>
  </AppDialog>
</template>
