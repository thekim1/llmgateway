<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ConfigDocument } from '@/api/types'
import { useOpsStore } from '@/stores/ops'
import { useUiStore } from '@/stores/ui'
import { problemLang, problemMessage } from '@/utils/problem'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import FormField from '@/components/ui/FormField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const ops = useOpsStore()
const ui = useUiStore()
const text = ref('')
const exporting = ref(false)
const error = ref<string | null>(null)
const errorLang = ref<'sv' | undefined>(undefined)
const confirmOpen = ref(false)
const parsed = ref<ConfigDocument | null>(null)
const empty = computed(() => text.value.trim() === '')

watch(
  () => props.open,
  (open) => {
    if (open) {
      error.value = null
      errorLang.value = undefined
    }
  },
)

async function exportConfig(): Promise<void> {
  exporting.value = true
  error.value = null
  try {
    text.value = JSON.stringify(await ops.exportConfig(), null, 2)
    ui.notify('Configuration exported to the text box.')
  } catch (e) {
    error.value = problemMessage(e)
    errorLang.value = problemLang(e)
  } finally {
    exporting.value = false
  }
}

function reviewImport(): void {
  error.value = null
  errorLang.value = undefined
  let value: unknown
  try {
    value = JSON.parse(text.value)
  } catch {
    error.value = 'This is not valid JSON. Check the text and try again.'
    return
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    error.value = 'The configuration must be a JSON object.'
    return
  }
  parsed.value = Object.fromEntries(Object.entries(value))
  confirmOpen.value = true
}

async function runImport(): Promise<void> {
  if (!parsed.value) return
  const result = await ops.importConfig(parsed.value)
  ui.notify(`Import finished: ${result.created} created, ${result.updated} updated, ${result.skipped} skipped.`)
  emit('update:open', false)
  await ops.loadHealth()
}
</script>

<template>
  <UiDialog :open="open" title="Configuration export and import" description="Export the gateway configuration as JSON, or paste a document to import. Metadata only; secrets are never included." @update:open="emit('update:open', $event)">
    <FormField v-slot="{ id, describedBy }" label="Configuration JSON" hint="Paste a configuration document, or export the current one.">
      <textarea :id="id" v-model="text" rows="12" spellcheck="false" :aria-describedby="describedBy" class="field-input font-mono" />
    </FormField>
    <InlineError v-if="error" :message="error" :lang="errorLang" />
    <p v-if="empty" class="text-small text-fg-3">Import is unavailable until the text box contains a configuration.</p>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Close</UiButton>
      <UiButton :loading="exporting" icon="download" @click="exportConfig">Export current</UiButton>
      <UiButton variant="primary" icon="upload" :disabled="empty" @click="reviewImport">Import</UiButton>
    </template>
  </UiDialog>
  <ConfirmDialog
    v-model:open="confirmOpen"
    title="Import configuration?"
    consequence="Existing entries with matching identifiers are updated. This is recorded in the audit log."
    confirm-label="Import configuration"
    :action="runImport"
  />
</template>
