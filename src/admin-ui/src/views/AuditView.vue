<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useOpsStore } from '@/stores/ops'
import { formatDateTime } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import Pagination from '@/components/ui/Pagination.vue'
import SelectField from '@/components/ui/SelectField.vue'

const PAGE_SIZE = 50
const ENTITY_TYPES = ['Department', 'Team', 'VirtualKey', 'Provider', 'Model', 'Route', 'RoutingRule', 'Budget', 'Config']
const entityOptions = [{ value: '', label: 'All entities' }, ...ENTITY_TYPES.map((t) => ({ value: t, label: t }))]

const columns: Column[] = [
  { key: 'timestamp', label: 'Time' },
  { key: 'actor', label: 'Actor' },
  { key: 'action', label: 'Action' },
  { key: 'entity', label: 'Entity' },
  { key: 'details', label: 'Details' },
]

const ops = useOpsStore()
const page = ref(1)
const entityType = ref('')

const load = () => ops.loadAudit({ page: page.value, pageSize: PAGE_SIZE, entityType: entityType.value || undefined })

function setEntity(value: string): void {
  entityType.value = value
  page.value = 1
  void load()
}

function setPage(next: number): void {
  page.value = next
  void load()
}

function detailsText(details: unknown): string {
  return typeof details === 'string' ? details : JSON.stringify(details, null, 2)
}

onMounted(load)
</script>

<template>
  <PageHeader title="Audit log" description="Who changed what, and when. Metadata only." />

  <div class="mb-4 max-w-xs">
    <SelectField label="Entity type" :model-value="entityType" :options="entityOptions" @update:model-value="setEntity" />
  </div>

  <AsyncState :loading="ops.auditLoading" :error="ops.auditError" @retry="load">
    <DataTable
      :columns="columns"
      :rows="ops.audit.items"
      :row-key="(e) => e.id"
      caption="Audit log entries"
      :clearable="entityType !== ''"
      empty-text="No audit entries match."
      @clear="setEntity('')"
    >
      <template #cell-timestamp="{ row }"><span class="tabular">{{ formatDateTime(row.timestamp, true) }}</span></template>
      <template #cell-actor="{ row }">{{ row.actor }}</template>
      <template #cell-action="{ row }">{{ row.action }}</template>
      <template #cell-entity="{ row }">
        {{ row.entityType }}<span v-if="row.entityId" class="font-mono text-small text-fg-3"> · {{ row.entityId }}</span>
      </template>
      <template #cell-details="{ row }">
        <details v-if="row.details">
          <summary class="cursor-pointer text-accent-ink">Show<span class="sr-only"> details for entry {{ row.id }}</span></summary>
          <pre class="mt-2 whitespace-pre-wrap break-words font-mono text-small">{{ detailsText(row.details) }}</pre>
        </details>
        <span v-else class="text-fg-3">—</span>
      </template>
    </DataTable>
    <Pagination :page="page" :page-size="PAGE_SIZE" :total="ops.audit.total" label="Audit log pages" @update:page="setPage" />
  </AsyncState>
</template>
