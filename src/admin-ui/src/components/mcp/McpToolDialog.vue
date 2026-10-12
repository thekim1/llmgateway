<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { McpServer, McpTool } from '@/api/types'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { MCP_TOOL_STATE } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import { isBlank, numberToInput, parseDecimal } from '@/utils/validation'
import DetailRow from '../ui/DetailRow.vue'
import InlineError from '../ui/InlineError.vue'
import TextField from '../ui/TextField.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'
import McpDefinitionDiff from './McpDefinitionDiff.vue'

const props = defineProps<{ open: boolean; server: McpServer; tool: McpTool | null }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'saved', tool: McpTool): void
  /** The tool changed on the server since it was shown: the caller reloads the tools. */
  (e: 'stale'): void
}>()

const ui = useUiStore()
const busy = ref<'approve' | 'reject' | 'price' | null>(null)
const error = ref<unknown>(null)
const notice = ref<string | null>(null)
const price = ref('')
const priceError = ref<string | null>(null)

const description = computed(() => (typeof props.tool?.definition?.description === 'string' ? props.tool.definition.description : null))
const definitionText = computed(() => JSON.stringify(props.tool?.definition ?? {}, null, 2))

watch(
  () => [props.open, props.tool?.id],
  () => {
    error.value = null
    notice.value = null
    priceError.value = null
    price.value = numberToInput(props.tool?.priceSek)
  },
  { immediate: true },
)

async function approve(): Promise<void> {
  const tool = props.tool
  if (!tool) return
  busy.value = 'approve'
  error.value = null
  notice.value = null
  try {
    emit('saved', await api.mcp.approve(props.server.id, tool.id, tool.definitionHash))
    ui.notify(`${tool.name} approved`)
    emit('update:open', false)
  } catch (e) {
    if (isApiError(e) && e.status === 409) {
      notice.value = 'The server changed this tool’s definition after you opened it, so nothing was approved. The new definition is shown below; review it and approve again.'
      emit('stale')
    } else {
      error.value = e
    }
  } finally {
    busy.value = null
  }
}

async function reject(): Promise<void> {
  const tool = props.tool
  if (!tool) return
  busy.value = 'reject'
  error.value = null
  try {
    emit('saved', await api.mcp.reject(props.server.id, tool.id))
    ui.notify(`${tool.name} rejected`)
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = null
  }
}

async function savePrice(): Promise<void> {
  const tool = props.tool
  if (!tool) return
  const value = parseDecimal(price.value)
  if (value !== null && (Number.isNaN(value) || value < 0 || value > 1_000_000)) {
    priceError.value = 'Enter a price from 0 to 1 000 000 kr, or leave it empty for free.'
    return
  }
  priceError.value = null
  busy.value = 'price'
  error.value = null
  try {
    emit('saved', await api.mcp.setPrice(props.server.id, tool.id, isBlank(price.value) ? null : value))
    ui.notify('Price saved')
  } catch (e) {
    error.value = e
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <UiDialog wide :open="open" :title="tool ? `Review ${tool.name}` : 'Tool'" @update:open="emit('update:open', $event)">
    <template v-if="tool">
      <div class="flex flex-col gap-1">
        <span><UiBadge :tone="MCP_TOOL_STATE[tool.approvalState].tone" :label="MCP_TOOL_STATE[tool.approvalState].label" /></span>
        <p class="text-small text-fg-2">{{ MCP_TOOL_STATE[tool.approvalState].hint }}</p>
      </div>
      <div aria-live="polite">
        <InlineError v-if="notice" tone="warn" title="The definition changed again" :message="notice" />
      </div>
      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
      <dl class="m-0">
        <DetailRow label="Name on /mcp"><span class="font-mono">{{ tool.exposedName }}</span></DetailRow>
        <DetailRow v-if="tool.title" label="Title">{{ tool.title }}</DetailRow>
        <DetailRow label="Description">{{ description ?? 'No description' }}</DetailRow>
        <DetailRow label="Discovered">{{ formatDateTime(tool.discoveredAt) }}</DetailRow>
        <DetailRow v-if="tool.approvedAt" label="Approved">{{ formatDateTime(tool.approvedAt) }} by {{ tool.approvedBy }}</DetailRow>
      </dl>
      <section v-if="tool.approvalState === 'Changed'" aria-labelledby="tool-diff-h" class="flex flex-col gap-2">
        <h3 id="tool-diff-h" class="text-heading">What changed</h3>
        <p class="text-small text-fg-2">Keys still see the approved definition. Read every change: a description is text the model follows.</p>
        <McpDefinitionDiff :changes="tool.changes" />
      </section>
      <details class="rounded-control border border-border p-3">
        <summary class="cursor-pointer font-medium">{{ tool.approvalState === 'Changed' ? 'Full definition offered now' : 'Full definition' }}</summary>
        <pre class="m-0 mt-2 max-h-80 overflow-auto rounded-chip bg-sunken p-2 font-mono text-small" tabindex="0">{{ definitionText }}</pre>
      </details>
      <form v-if="server.canManage" class="flex flex-wrap items-end gap-2" novalidate @submit.prevent="savePrice">
        <TextField v-model="price" label="Price per call (kr)" inputmode="decimal" :error="priceError" hint="Charged to the caller’s budgets. Empty = free." class="w-56" />
        <UiButton type="submit" size="sm" :loading="busy === 'price'">Save price</UiButton>
      </form>
    </template>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Close</UiButton>
      <template v-if="tool && server.canManage">
        <UiButton v-if="tool.approvalState !== 'Rejected'" variant="danger" :loading="busy === 'reject'" @click="reject">Reject</UiButton>
        <UiButton v-if="tool.approvalState !== 'Approved'" variant="primary" icon="check" :loading="busy === 'approve'" @click="approve">
          {{ tool.approvalState === 'Changed' ? 'Approve the change' : 'Approve' }}
        </UiButton>
      </template>
    </template>
  </UiDialog>
</template>
