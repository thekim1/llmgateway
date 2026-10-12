<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { McpServer } from '@/api/types'
import { useSortedPage } from '@/composables/useTableView'
import McpServerDrawer from '@/components/mcp/McpServerDrawer.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import Pagination from '@/components/ui/Pagination.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useMcpStore } from '@/stores/mcp'
import { formatDateTime, formatRelative } from '@/utils/format'
import { MCP_STATUS, plural } from '@/utils/labels'

const mcp = useMcpStore()
const drawer = ref(false)
const serverId = ref<string | null>(null)
const editing = computed(() => mcp.servers.find((s) => s.id === serverId.value) ?? null)

const ALL = 'all'
const search = ref('')
const status = ref(ALL)
const statusOptions = [
  { value: ALL, label: 'All statuses' },
  { value: 'attention', label: 'Needs approval' },
  { value: 'Healthy', label: 'Healthy' },
  { value: 'Degraded', label: 'Degraded' },
  { value: 'Unavailable', label: 'Unavailable' },
  { value: 'disabled', label: 'Disabled' },
]

const waiting = (s: McpServer) => s.pendingToolCount + s.changedToolCount
const filtered = computed(() => {
  const q = search.value.trim().toLowerCase()
  return mcp.servers.filter((s) => {
    if (status.value === 'attention' && waiting(s) === 0) return false
    if (status.value === 'disabled' && s.isEnabled) return false
    if (['Healthy', 'Degraded', 'Unavailable'].includes(status.value) && s.status !== status.value) return false
    return !q || [s.slug, s.displayName, s.url, s.ownerDepartmentName].some((v) => v?.toLowerCase().includes(q))
  })
})
const filtering = computed(() => search.value.trim() !== '' || status.value !== ALL)
const view = useSortedPage(
  filtered,
  {
    name: (s) => s.slug,
    owner: (s) => s.ownerDepartmentName ?? '',
    status: (s) => (s.isEnabled ? ['Healthy', 'Degraded', 'Unavailable'].indexOf(s.status) : 3),
    tools: (s) => s.toolCount,
    waiting: (s) => -waiting(s),
  },
  { key: 'name' },
)
const page = view.page

const columns: Column[] = [
  { key: 'name', label: 'Server', sortable: true },
  { key: 'owner', label: 'Owner', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
  { key: 'tools', label: 'Tools', sortable: true },
  { key: 'waiting', label: 'Approval', sortable: true },
]

function open(server: McpServer | null): void {
  serverId.value = server?.id ?? null
  drawer.value = true
}

function clearFilters(): void {
  search.value = ''
  status.value = ALL
}

onMounted(() => void mcp.load())
</script>

<template>
  <PageHeader
    title="MCP servers"
    description="Tool servers clients reach through the gateway. Each tool is hidden until an admin approves its definition, and keys need a grant."
  >
    <UiButton variant="primary" icon="add" @click="open(null)">New MCP server</UiButton>
  </PageHeader>

  <AsyncState :loading="mcp.loading" :error="mcp.error" :empty="!mcp.loaded" @retry="mcp.load">
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-center gap-3">
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="mcp-search" class="sr-only">Search MCP servers</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="mcp-search" v-model="search" type="search" autocomplete="off" placeholder="Search by name, URL or owner" class="field-input w-full !pl-10" />
        </div>
        <FilterSelect v-model="status" label="Status" :options="statusOptions" />
      </div>

      <DataTable
        :columns="columns"
        :rows="view.paged.value"
        :row-key="(s) => s.id"
        caption="MCP servers"
        clickable
        :clearable="filtering"
        :sort-key="view.sortKey.value"
        :sort-dir="view.sortDir.value"
        :empty-text="filtering ? 'No MCP servers match your filters.' : 'No MCP servers yet. Add one, sync it and approve its tools.'"
        @row-click="open"
        @sort="view.toggleSort"
        @clear="clearFilters"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.slug }}</span>
            <span class="text-caption text-fg-3">{{ row.displayName }}</span>
            <ResidencyLabel :residency="row.residency" />
          </span>
        </template>
        <template #cell-owner="{ row }">
          <span v-if="row.ownerDepartmentName" lang="sv">{{ row.ownerDepartmentName }}</span>
          <span v-else class="text-fg-2">Global</span>
        </template>
        <template #cell-status="{ row }">
          <span class="flex flex-col items-start gap-1">
            <span class="flex flex-wrap gap-1.5">
              <UiBadge v-if="!row.isEnabled" tone="neutral" label="Disabled" />
              <UiBadge v-if="row.lastCheckedAt" :tone="MCP_STATUS[row.status].tone" :label="MCP_STATUS[row.status].label" />
              <UiBadge v-else tone="neutral" label="Not synced yet" />
            </span>
            <span v-if="row.lastCheckedAt" class="text-caption text-fg-3" :title="formatDateTime(row.lastCheckedAt, true)">Checked {{ formatRelative(row.lastCheckedAt) }}</span>
            <span v-if="row.lastError" class="line-clamp-2 max-w-xs text-caption text-fg-2" lang="sv">{{ row.lastError }}</span>
          </span>
        </template>
        <template #cell-tools="{ row }">
          <span class="flex flex-col text-small">
            <span>{{ plural(row.toolCount, 'tool') }}</span>
            <span v-if="row.pendingToolCount" class="text-fg-2">{{ row.pendingToolCount }} new</span>
            <span v-if="row.changedToolCount" class="text-fg-2">{{ row.changedToolCount }} changed upstream</span>
          </span>
        </template>
        <template #cell-waiting="{ row }">
          <UiBadge v-if="waiting(row) > 0 && row.canManage" tone="warn" :label="`${waiting(row)} to review`" />
          <span v-else-if="waiting(row) > 0" class="text-small text-fg-2">{{ waiting(row) }} waiting for a gateway admin</span>
          <span v-else class="text-small text-fg-3">Nothing to review</span>
        </template>
      </DataTable>

      <Pagination v-if="view.sorted.value.length > view.pageSize" v-model:page="page" :page-size="view.pageSize" :total="view.sorted.value.length" label="MCP server pages" />
      <p v-if="filtering && mcp.servers.length > 0" class="text-small text-fg-3">Showing {{ filtered.length }} of {{ mcp.servers.length }} servers.</p>
    </div>
  </AsyncState>

  <McpServerDrawer v-model:open="drawer" :server="editing" @created="(s) => (serverId = s.id)" />
</template>
