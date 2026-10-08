<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { Model, Provider } from '@/api/types'
import ModelFormDrawer from '@/components/providers/ModelFormDrawer.vue'
import ProviderFormDrawer from '@/components/providers/ProviderFormDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useModelsStore } from '@/stores/models'
import { useProvidersStore } from '@/stores/providers'
import { formatNumber, formatUsd } from '@/utils/format'
import { KIND_LABEL, PROVIDER_TYPE_LABEL, plural } from '@/utils/labels'

type Segment = 'providers' | 'models'

const providers = useProvidersStore()
const models = useModelsStore()

const segment = ref<Segment>('providers')
const providerDrawer = ref(false)
const modelDrawer = ref(false)
const providerId = ref<string | null>(null)
const modelId = ref<string | null>(null)

const editingProvider = computed(() => providers.items.find((p) => p.id === providerId.value) ?? null)
const editingModel = computed(() => models.items.find((m) => m.id === modelId.value) ?? null)
const noProviders = computed(() => providers.items.length === 0)

const segments = computed(() => [
  { value: 'providers' as Segment, label: 'Providers', count: providers.items.length },
  { value: 'models' as Segment, label: 'Models', count: models.items.length },
])

const providerColumns: Column[] = [
  { key: 'name', label: 'Provider' },
  { key: 'type', label: 'Type' },
  { key: 'residency', label: 'Residency' },
  { key: 'status', label: 'Status' },
  { key: 'models', label: 'Models', align: 'right' },
]
const modelColumns: Column[] = [
  { key: 'name', label: 'Model' },
  { key: 'provider', label: 'Provider' },
  { key: 'kind', label: 'Kind' },
  { key: 'context', label: 'Context', align: 'right' },
  { key: 'price', label: 'USD per 1M tokens (in / out)', align: 'right' },
  { key: 'status', label: 'Status' },
]

async function load(): Promise<void> {
  await Promise.all([providers.load(), models.load()])
}

function openProvider(provider: Provider | null): void {
  providerId.value = provider?.id ?? null
  providerDrawer.value = true
}
function openModel(model: Model | null): void {
  modelId.value = model?.id ?? null
  modelDrawer.value = true
}
function create(): void {
  if (segment.value === 'providers') openProvider(null)
  else openModel(null)
}

onMounted(load)
</script>

<template>
  <PageHeader title="Providers & models" description="Providers are the services that run models. Routes send requests to their models.">
    <span v-if="segment === 'models' && noProviders" id="new-model-reason" class="text-small text-fg-3">Add a provider first.</span>
    <UiButton
      variant="primary"
      icon="add"
      :disabled="segment === 'models' && noProviders"
      :aria-describedby="segment === 'models' && noProviders ? 'new-model-reason' : undefined"
      @click="create"
    >{{ segment === 'providers' ? 'New provider' : 'New model' }}</UiButton>
  </PageHeader>

  <AsyncState :loading="providers.loading || models.loading" :error="providers.error ?? models.error" :empty="!providers.loaded && !models.loaded" @retry="load">
    <div class="flex flex-col gap-4">
      <SegmentedControl v-model="segment" label="Show" :options="segments" />

      <DataTable
        v-if="segment === 'providers'"
        :columns="providerColumns"
        :rows="providers.items"
        :row-key="(p) => p.id"
        caption="Providers"
        clickable
        empty-text="No providers yet. Add one to start serving models."
        @row-click="openProvider"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.displayName" class="text-caption text-fg-3">{{ row.displayName }}</span>
          </span>
        </template>
        <template #cell-type="{ row }">{{ PROVIDER_TYPE_LABEL[row.type] }}</template>
        <template #cell-residency="{ row }"><ResidencyLabel :residency="row.residency" /></template>
        <template #cell-status="{ row }">
          <span class="flex flex-wrap gap-1.5">
            <UiBadge :tone="row.isEnabled ? 'ok' : 'neutral'" :label="row.isEnabled ? 'Enabled' : 'Disabled'" />
            <UiBadge v-if="row.isDrained" tone="neutral" label="Drained" />
          </span>
        </template>
        <template #cell-models="{ row }">{{ row.deploymentCount }}</template>
      </DataTable>

      <DataTable
        v-else
        :columns="modelColumns"
        :rows="models.items"
        :row-key="(m) => m.id"
        caption="Models"
        clickable
        empty-text="No models yet. Add one so routes have something to send requests to."
        @row-click="openModel"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span class="font-mono text-caption text-fg-3">{{ row.upstreamModel }}</span>
          </span>
        </template>
        <template #cell-provider="{ row }">
          <span class="flex flex-col">
            <span>{{ row.providerName }}</span>
            <ResidencyLabel :residency="row.residency" />
          </span>
        </template>
        <template #cell-kind="{ row }">{{ KIND_LABEL[row.kind] }}</template>
        <template #cell-context="{ row }">{{ row.contextWindow == null ? '—' : formatNumber(row.contextWindow) }}</template>
        <template #cell-price="{ row }">
          <span v-if="row.currentPrice">{{ formatUsd(row.currentPrice.inputPerMillionUsd) }} / {{ formatUsd(row.currentPrice.outputPerMillionUsd) }}</span>
          <span v-else class="text-fg-3">No price</span>
        </template>
        <template #cell-status="{ row }"><UiBadge :tone="row.isEnabled ? 'ok' : 'neutral'" :label="row.isEnabled ? 'Enabled' : 'Disabled'" /></template>
      </DataTable>

      <p v-if="segment === 'providers' && providers.items.length > 0" class="text-small text-fg-3">
        {{ plural(providers.items.reduce((n, p) => n + p.deploymentCount, 0), 'model') }} across {{ plural(providers.items.length, 'provider') }}.
      </p>
    </div>
  </AsyncState>

  <ProviderFormDrawer v-model:open="providerDrawer" :provider="editingProvider" />
  <ModelFormDrawer v-model:open="modelDrawer" :model="editingModel" :providers="providers.items" />
</template>
