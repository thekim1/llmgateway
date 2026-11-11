<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { Integration } from '@/api/types'
import { useIntegrationsStore } from '@/stores/integrations'
import { useUiStore } from '@/stores/ui'
import { plural } from '@/utils/labels'
import { formatDate } from '@/utils/format'
import IntegrationFormDrawer from '@/components/integrations/IntegrationFormDrawer.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

const store = useIntegrationsStore()
const ui = useUiStore()

const formOpen = ref(false)
const editing = ref<Integration | null>(null)
const target = ref<Integration | null>(null)
const deleteOpen = ref(false)

const columns: Column[] = [
  { key: 'integration', label: 'Integration' },
  { key: 'owner', label: 'Owner team' },
  { key: 'keys', label: 'Keys' },
  { key: 'status', label: 'Status' },
  { key: 'actions', label: 'Actions', hideLabel: true, align: 'right' },
]

const summary = computed(
  () =>
    `${plural(store.items.length, 'integration')}. An integration is another system, such as a RAG platform, that uses gateway keys for the teams it serves; each team's usage stays on its own key.`,
)

function openCreate(): void {
  editing.value = null
  formOpen.value = true
}

function openEdit(integration: Integration): void {
  editing.value = integration
  formOpen.value = true
}

function confirmDelete(integration: Integration): void {
  target.value = integration
  deleteOpen.value = true
}

function onSaved(integration: Integration, created: boolean): void {
  ui.notify(created ? `${integration.name} created` : `${integration.name} saved`)
}

async function remove(): Promise<void> {
  if (!target.value) return
  const name = target.value.name
  await store.remove(target.value.id)
  ui.notify(`${name} deleted`)
}

onMounted(() => {
  void store.load()
})
</script>

<template>
  <PageHeader title="Integrations" :description="summary">
    <UiButton variant="primary" icon="add" @click="openCreate">New integration</UiButton>
  </PageHeader>

  <p class="mb-4 flex items-start gap-2 text-small text-fg-2">
    <AppIcon name="info" :size="18" class="mt-px shrink-0" />
    <span>
      Credential keys are what the integration’s services sign in with; create them on the Keys page in the owner team. Department and team
      admins create assigned keys for their own chatbots and knowledge bases there too, and set their limits and budgets. See docs/integrations.md.
    </span>
  </p>

  <AsyncState
    :loading="store.loading"
    :error="store.error"
    :empty="store.items.length === 0"
    empty-text="No integrations yet. Create one for a system that serves several departments, such as a RAG platform."
    @retry="store.load()"
  >
    <DataTable :columns="columns" :rows="store.items" :row-key="(i) => i.id" caption="Integrations">
      <template #cell-integration="{ row }">
        <span class="block font-medium">{{ row.name }}</span>
        <span v-if="row.description" class="block text-caption text-fg-3">{{ row.description }}</span>
        <span class="block text-caption text-fg-3">Created {{ formatDate(row.createdAt) }}</span>
      </template>
      <template #cell-owner="{ row }">
        <span class="block" lang="sv">{{ row.ownerTeamName }}</span>
        <span class="block text-caption text-fg-3" lang="sv">{{ row.ownerDepartmentName }}</span>
      </template>
      <template #cell-keys="{ row }">
        <span class="block tabular">{{ plural(row.credentialKeys, 'credential key') }}</span>
        <span class="block text-caption text-fg-3 tabular">{{ plural(row.assignedKeys, 'assigned key') }}</span>
      </template>
      <template #cell-status="{ row }">
        <UiBadge :tone="row.isActive ? 'ok' : 'neutral'" :label="row.isActive ? 'Active' : 'Inactive'" />
      </template>
      <template #cell-actions="{ row }">
        <span class="flex flex-wrap justify-end gap-2">
          <UiButton size="sm" icon="edit" @click="openEdit(row)">Edit<span class="sr-only"> {{ row.name }}</span></UiButton>
          <UiButton size="sm" variant="danger" icon="delete" @click="confirmDelete(row)">Delete<span class="sr-only"> {{ row.name }}</span></UiButton>
        </span>
      </template>
    </DataTable>
  </AsyncState>

  <IntegrationFormDrawer v-model:open="formOpen" :existing="editing" @saved="onSaved" />
  <ConfirmDialog
    v-model:open="deleteOpen"
    :title="`Delete ${target?.name ?? 'integration'}?`"
    consequence="Only possible when all its keys are revoked. Revoked keys keep their history; the audit log keeps the integration’s."
    confirm-label="Delete integration"
    danger
    :action="remove"
  />
</template>
