<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { McpServer, McpSyncResult, McpTool, McpToolState } from '@/api/types'
import { replaceById } from '@/stores/loadable'
import { useMcpStore } from '@/stores/mcp'
import { formatDateTime, formatSek } from '@/utils/format'
import { MCP_TOOL_STATE, plural } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import AsyncState from '../ui/AsyncState.vue'
import DataTable, { type Column } from '../ui/DataTable.vue'
import InlineError from '../ui/InlineError.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import McpToolDialog from './McpToolDialog.vue'

const props = defineProps<{ server: McpServer }>()
const mcp = useMcpStore()

const tools = ref<McpTool[]>([])
const loading = ref(false)
const error = ref<unknown>(null)
const syncBusy = ref(false)
const syncResult = ref<McpSyncResult | null>(null)
const syncError = ref<unknown>(null)
const selectedId = ref<string | null>(null)
const dialogOpen = ref(false)
const selected = computed(() => tools.value.find((t) => t.id === selectedId.value) ?? null)

const ORDER: McpToolState[] = ['Changed', 'Pending', 'Approved', 'Rejected']
const sorted = computed(() => [...tools.value].sort((a, b) => ORDER.indexOf(a.approvalState) - ORDER.indexOf(b.approvalState) || a.name.localeCompare(b.name)))
const columns: Column[] = [
  { key: 'name', label: 'Tool' },
  { key: 'state', label: 'State' },
  { key: 'exposed', label: 'Name on /mcp' },
  { key: 'price', label: 'Price per call', align: 'right' },
]

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    tools.value = await api.mcp.tools(props.server.id)
  } catch (e) {
    error.value = e
  } finally {
    loading.value = false
  }
}

async function sync(): Promise<void> {
  syncBusy.value = true
  syncResult.value = null
  syncError.value = null
  try {
    syncResult.value = await mcp.sync(props.server.id)
    await load()
  } catch (e) {
    syncError.value = e
  } finally {
    syncBusy.value = false
  }
}

const syncSummary = computed(() => {
  const r = syncResult.value
  if (!r) return ''
  const parts = [
    r.added.length && `${plural(r.added.length, 'new tool')}`,
    r.changed.length && `${r.changed.length} changed upstream`,
    r.reverted.length && `${r.reverted.length} back to the approved definition`,
    r.updated.length && `${r.updated.length} updated`,
    r.removed.length && `${r.removed.length} removed`,
  ].filter(Boolean)
  return parts.length ? `Synced: ${parts.join(', ')}.` : 'Synced. Nothing changed.'
})

function review(tool: McpTool): void {
  selectedId.value = tool.id
  dialogOpen.value = true
}

function saved(tool: McpTool): void {
  tools.value = replaceById(tools.value, tool)
  void mcp.load()
}

watch(() => props.server.id, load, { immediate: true })
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <p class="text-small text-fg-2">
        <template v-if="server.lastCheckedAt">Last synced {{ formatDateTime(server.lastCheckedAt) }}.</template>
        <template v-else>Not synced yet. Sync to discover the server’s tools.</template>
      </p>
      <UiButton v-if="server.canManage" icon="sync" :loading="syncBusy" @click="sync">Sync now</UiButton>
    </div>
    <div aria-live="polite" class="flex flex-col gap-2" data-testid="mcp-sync-result">
      <template v-if="syncResult">
        <p class="rounded-control bg-sunken p-3 text-small">{{ syncSummary }}</p>
        <InlineError v-if="syncResult.lastError" :tone="syncResult.status === 'Unavailable' ? 'danger' : 'warn'" :message="syncResult.lastError" lang="sv" />
      </template>
      <InlineError v-else-if="syncError" :message="problemMessage(syncError)" :lang="problemLang(syncError)" />
    </div>
    <InlineError v-if="!syncResult && server.lastError" tone="warn" title="Last sync" :message="server.lastError" lang="sv" />

    <AsyncState :loading="loading" :error="error" :empty="false" @retry="load">
      <DataTable
        :columns="columns"
        :rows="sorted"
        :row-key="(t) => t.id"
        caption="Tools"
        clickable
        :empty-text="server.lastCheckedAt ? 'The server offers no tools.' : 'No tools yet. Sync the server to discover them.'"
        @row-click="review"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.title" class="text-caption text-fg-3">{{ row.title }}</span>
          </span>
        </template>
        <template #cell-state="{ row }"><UiBadge :tone="MCP_TOOL_STATE[row.approvalState].tone" :label="MCP_TOOL_STATE[row.approvalState].label" /></template>
        <template #cell-exposed="{ row }"><span class="font-mono text-small">{{ row.exposedName }}</span></template>
        <template #cell-price="{ row }">{{ row.priceSek === null ? 'Free' : formatSek(row.priceSek) }}</template>
      </DataTable>
    </AsyncState>
    <p v-if="server.canManage && tools.some((t) => t.approvalState === 'Pending' || t.approvalState === 'Changed')" class="text-small text-fg-2">
      Select a tool to review its definition and approve or reject it.
    </p>

    <McpToolDialog v-model:open="dialogOpen" :server="server" :tool="selected" @saved="saved" @stale="load" />
  </div>
</template>
