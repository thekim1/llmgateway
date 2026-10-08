<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CreateKeyResponse, KeyStatus, RotateKeyResponse, VirtualKey } from '@/api/types'
import { useKeysStore } from '@/stores/keys'
import { useUiStore } from '@/stores/ui'
import { KEY_STATUS, modelsLabel, plural } from '@/utils/labels'
import { formatRelative } from '@/utils/format'
import KeyDetailDrawer from '@/components/keys/KeyDetailDrawer.vue'
import KeyFormDrawer from '@/components/keys/KeyFormDrawer.vue'
import RotateKeyDialog from '@/components/keys/RotateKeyDialog.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import KeyStatusBadge from '@/components/ui/KeyStatusBadge.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import SecretDialog from '@/components/ui/SecretDialog.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiButton from '@/components/ui/UiButton.vue'

type StatusFilter = 'all' | KeyStatus

const keys = useKeysStore()
const ui = useUiStore()

const status = ref<StatusFilter>('all')
const search = ref('')
const selectedId = ref<string | null>(null)
const detailOpen = ref(false)
const formOpen = ref(false)
const editing = ref<VirtualKey | null>(null)
const rotateOpen = ref(false)
const revokeOpen = ref(false)
const secretOpen = ref(false)
// Kept in memory only and cleared as soon as the dialog closes.
const secret = ref('')

const columns: Column[] = [
  { key: 'key', label: 'Key' },
  { key: 'owner', label: 'Owner' },
  { key: 'status', label: 'Status' },
  { key: 'access', label: 'Access' },
  { key: 'lastUsed', label: 'Last used' },
]

const selected = computed(() => keys.items.find((k) => k.id === selectedId.value) ?? null)

const statusOptions = computed(() => {
  const count = (s: KeyStatus) => keys.items.filter((k) => k.status === s).length
  const options: { value: StatusFilter; label: string; count: number }[] = [{ value: 'all', label: 'All', count: keys.items.length }]
  for (const s of ['Active', 'InGracePeriod', 'Expired', 'Revoked', 'Disabled'] as const) {
    const n = count(s)
    // Disabled is rare, so it only appears when a key has that status.
    if (s !== 'Disabled' || n > 0 || status.value === s) options.push({ value: s, label: KEY_STATUS[s].label, count: n })
  }
  return options
})

const filtered = computed(() => {
  const query = search.value.trim().toLowerCase()
  return keys.items.filter((k) => {
    if (status.value !== 'all' && k.status !== status.value) return false
    if (!query) return true
    return [k.name, k.prefix, k.teamName, k.departmentName].some((v) => v.toLowerCase().includes(query))
  })
})

const summary = computed(() => {
  const active = keys.items.filter((k) => k.status === 'Active').length
  return `${plural(keys.items.length, 'key')}, ${active} active.`
})

const filtering = computed(() => status.value !== 'all' || search.value.trim() !== '')

function clearFilters(): void {
  status.value = 'all'
  search.value = ''
}

function openDetail(key: VirtualKey): void {
  selectedId.value = key.id
  detailOpen.value = true
}

function openCreate(): void {
  editing.value = null
  formOpen.value = true
}

function openEdit(): void {
  editing.value = selected.value
  detailOpen.value = false
  formOpen.value = true
}

function showSecret(value: string): void {
  secret.value = value
  secretOpen.value = true
}

function onCreated(result: CreateKeyResponse): void {
  selectedId.value = result.key.id
  showSecret(result.secret)
}

function onSaved(key: VirtualKey): void {
  ui.notify(`${key.name} saved`)
  openDetail(key)
}

function onRotated(result: RotateKeyResponse): void {
  selectedId.value = result.key.id
  showSecret(result.secret)
}

function onSecretDone(): void {
  ui.notify('Key ready to use')
}

watch(secretOpen, (open) => {
  if (!open) secret.value = ''
})

async function revoke(): Promise<void> {
  if (!selected.value) return
  const key = await keys.revoke(selected.value.id)
  ui.notify(`${key.name} revoked`)
}

onMounted(() => {
  void keys.load()
})
onBeforeUnmount(() => {
  secret.value = ''
})
</script>

<template>
  <PageHeader title="Keys" :description="summary">
    <UiButton variant="primary" icon="add" @click="openCreate">New key</UiButton>
  </PageHeader>

  <div class="mb-4 flex flex-wrap items-center gap-3">
    <div class="relative min-w-[240px] flex-1 max-w-sm">
      <label for="key-search" class="sr-only">Search keys</label>
      <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
      <input id="key-search" v-model="search" type="search" autocomplete="off" placeholder="Search by name, prefix or team" class="field-input w-full !pl-10" />
    </div>
    <SegmentedControl v-model="status" label="Filter by status" :options="statusOptions" />
  </div>

  <AsyncState :loading="keys.loading" :error="keys.error" :empty="keys.items.length === 0" empty-text="No keys yet. Create one to give an application access." @retry="keys.load()">
    <DataTable
      :columns="columns"
      :rows="filtered"
      :row-key="(k) => k.id"
      caption="Virtual keys"
      clickable
      clearable
      :selected-key="detailOpen ? selectedId : null"
      empty-text="No keys match your filters."
      @row-click="openDetail"
      @clear="clearFilters"
    >
      <template #cell-key="{ row }">
        <span class="block font-medium">{{ row.name }}</span>
        <span class="block font-mono text-caption text-fg-3">{{ row.prefix }}</span>
      </template>
      <template #cell-owner="{ row }">
        <span class="block" lang="sv">{{ row.teamName }}</span>
        <span class="block text-caption text-fg-3" lang="sv">{{ row.departmentName }}</span>
      </template>
      <template #cell-status="{ row }"><KeyStatusBadge :status="row.status" /></template>
      <template #cell-access="{ row }">
        <span class="block max-w-64 truncate font-mono text-small" :class="{ 'font-sans': row.allowedModels.length === 0 }">{{ modelsLabel(row.allowedModels) }}</span>
        <span class="block text-caption">
          <span v-if="row.allowedResidencies.length === 0" class="text-fg-3">Any residency</span>
          <span v-else class="flex flex-wrap gap-x-2"><ResidencyLabel v-for="r in row.allowedResidencies" :key="r" :residency="r" /></span>
        </span>
      </template>
      <template #cell-lastUsed="{ row }"><span class="text-fg-2">{{ row.lastUsedAt ? formatRelative(row.lastUsedAt) : 'Never used' }}</span></template>
    </DataTable>
    <p v-if="filtering && filtered.length > 0" class="mt-3 text-small text-fg-3">Showing {{ filtered.length }} of {{ keys.items.length }}.</p>
  </AsyncState>

  <KeyDetailDrawer v-model:open="detailOpen" :key-data="selected" @rotate="rotateOpen = true" @edit="openEdit" @revoke="revokeOpen = true" />
  <KeyFormDrawer v-model:open="formOpen" :existing="editing" @created="onCreated" @saved="onSaved" />
  <RotateKeyDialog v-model:open="rotateOpen" :key-data="selected" @rotated="onRotated" />
  <ConfirmDialog
    v-model:open="revokeOpen"
    :title="`Revoke ${selected?.name ?? 'key'}?`"
    consequence="Requests fail with key_revoked straight away. This can’t be undone."
    confirm-label="Revoke key"
    danger
    :action="revoke"
  />
  <SecretDialog v-model:open="secretOpen" :secret="secret" @done="onSecretDone" />
</template>
