<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { Model, Provider } from '@/api/types'
import { useSortedPage } from '@/composables/useTableView'
import ModelFormDrawer from '@/components/providers/ModelFormDrawer.vue'
import ProviderFormDrawer from '@/components/providers/ProviderFormDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import Pagination from '@/components/ui/Pagination.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useModelsStore } from '@/stores/models'
import { useProvidersStore } from '@/stores/providers'
import { formatNumber, formatUsd } from '@/utils/format'
import { KIND_LABEL, MODEL_FEATURES, PROVIDER_TYPE_LABEL, RESIDENCY, featureLabel, plural } from '@/utils/labels'

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
  { key: 'name', label: 'Provider', sortable: true },
  { key: 'type', label: 'Type', sortable: true },
  { key: 'residency', label: 'Residency', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
  { key: 'models', label: 'Models', align: 'right', sortable: true },
]
const modelColumns: Column[] = [
  { key: 'name', label: 'Model', sortable: true },
  { key: 'provider', label: 'Provider', sortable: true },
  { key: 'kind', label: 'Kind', sortable: true },
  { key: 'features', label: 'Capabilities' },
  { key: 'context', label: 'Context', align: 'right', sortable: true },
  { key: 'price', label: 'USD per 1M tokens (in / out)', align: 'right', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
]

const ALL = 'all'
const withAll = (label: string, options: { value: string; label: string }[]) => [{ value: ALL, label }, ...options]
const includes = (query: string, ...values: (string | null | undefined)[]) => values.some((v) => v?.toLowerCase().includes(query))

// Providers: search, filters, sorting
const providerSearch = ref('')
const providerType = ref(ALL)
const providerResidency = ref(ALL)
const providerStatus = ref(ALL)

const providerTypeOptions = computed(() =>
  withAll('All types', [...new Set(providers.items.map((p) => p.type))].map((t) => ({ value: t, label: PROVIDER_TYPE_LABEL[t] })).sort((a, b) => a.label.localeCompare(b.label))),
)
const residencyOptions = computed(() =>
  withAll('All residencies', [...new Set([...providers.items.map((p) => p.residency), ...models.items.map((m) => m.residency)])].map((r) => ({ value: r, label: RESIDENCY[r].label }))),
)
const statusOptions = withAll('All statuses', [
  { value: 'enabled', label: 'Enabled' },
  { value: 'disabled', label: 'Disabled' },
  { value: 'drained', label: 'Drained' },
])

const filteredProviders = computed(() => {
  const q = providerSearch.value.trim().toLowerCase()
  return providers.items.filter((p) => {
    if (providerType.value !== ALL && p.type !== providerType.value) return false
    if (providerResidency.value !== ALL && p.residency !== providerResidency.value) return false
    if (providerStatus.value === 'enabled' && !p.isEnabled) return false
    if (providerStatus.value === 'disabled' && p.isEnabled) return false
    if (providerStatus.value === 'drained' && !p.isDrained) return false
    return !q || includes(q, p.name, p.displayName, p.baseUrl, PROVIDER_TYPE_LABEL[p.type])
  })
})
const providerView = useSortedPage(
  filteredProviders,
  {
    name: (p) => p.name,
    type: (p) => PROVIDER_TYPE_LABEL[p.type],
    residency: (p) => RESIDENCY[p.residency].label,
    status: (p) => (p.isEnabled ? (p.isDrained ? 1 : 0) : 2),
    models: (p) => p.deploymentCount,
  },
  { key: 'name' },
)
const providerPage = providerView.page
const providersFiltering = computed(() => providerSearch.value.trim() !== '' || providerType.value !== ALL || providerResidency.value !== ALL || providerStatus.value !== ALL)
function clearProviderFilters(): void {
  providerSearch.value = ''
  providerType.value = providerResidency.value = providerStatus.value = ALL
}

// Models: search, filters, sorting
const modelSearch = ref('')
const modelProvider = ref(ALL)
const modelKind = ref(ALL)
const modelFeature = ref(ALL)
const modelStatus = ref(ALL)
const modelPrice = ref(ALL)

const modelProviderOptions = computed(() =>
  withAll('All providers', [...new Map(models.items.map((m) => [m.providerId, m.providerName])).entries()].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))),
)
const kindOptions = withAll('All kinds', Object.entries(KIND_LABEL).map(([value, label]) => ({ value, label })))
const featureOptions = computed(() => {
  const present = new Set(models.items.flatMap((m) => m.features))
  const known = MODEL_FEATURES.filter((f) => present.has(f))
  const other = [...present].filter((f) => !(MODEL_FEATURES as readonly string[]).includes(f)).sort()
  return withAll('Any capability', [...known, ...other].map((f) => ({ value: f, label: featureLabel(f) })))
})
const modelStatusOptions = withAll('All statuses', [
  { value: 'enabled', label: 'Enabled' },
  { value: 'disabled', label: 'Disabled' },
])
const priceOptions = withAll('Any price', [
  { value: 'priced', label: 'Has price' },
  { value: 'unpriced', label: 'No price' },
])

const filteredModels = computed(() => {
  const q = modelSearch.value.trim().toLowerCase()
  return models.items.filter((m) => {
    if (modelProvider.value !== ALL && m.providerId !== modelProvider.value) return false
    if (modelKind.value !== ALL && m.kind !== modelKind.value) return false
    if (modelFeature.value !== ALL && !m.features.includes(modelFeature.value)) return false
    if (modelStatus.value === 'enabled' && !m.isEnabled) return false
    if (modelStatus.value === 'disabled' && m.isEnabled) return false
    if (modelPrice.value === 'priced' && !m.currentPrice) return false
    if (modelPrice.value === 'unpriced' && m.currentPrice) return false
    return !q || includes(q, m.name, m.upstreamModel, m.providerName, ...m.features)
  })
})
const modelView = useSortedPage(
  filteredModels,
  {
    name: (m) => m.name,
    provider: (m) => m.providerName,
    kind: (m) => m.kind,
    context: (m) => m.contextWindow,
    price: (m) => m.currentPrice?.inputPerMillionUsd,
    status: (m) => (m.isEnabled ? 0 : 1),
  },
  { key: 'name' },
)
const modelPage = modelView.page
const modelsFiltering = computed(
  () => modelSearch.value.trim() !== '' || [modelProvider, modelKind, modelFeature, modelStatus, modelPrice].some((f) => f.value !== ALL),
)
function clearModelFilters(): void {
  modelSearch.value = ''
  modelProvider.value = modelKind.value = modelFeature.value = modelStatus.value = modelPrice.value = ALL
}

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

      <div v-if="segment === 'providers'" class="flex flex-wrap items-center gap-3">
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="provider-search" class="sr-only">Search providers</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="provider-search" v-model="providerSearch" type="search" autocomplete="off" placeholder="Search by name, type or URL" class="field-input w-full !pl-10" />
        </div>
        <FilterSelect v-model="providerType" label="Type" :options="providerTypeOptions" />
        <FilterSelect v-model="providerResidency" label="Residency" :options="residencyOptions" />
        <FilterSelect v-model="providerStatus" label="Status" :options="statusOptions" />
      </div>
      <div v-else class="flex flex-wrap items-center gap-3">
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="model-search" class="sr-only">Search models</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="model-search" v-model="modelSearch" type="search" autocomplete="off" placeholder="Search by model, upstream name, provider or capability" class="field-input w-full !pl-10" />
        </div>
        <FilterSelect v-model="modelProvider" label="Provider" :options="modelProviderOptions" />
        <FilterSelect v-model="modelKind" label="Kind" :options="kindOptions" />
        <FilterSelect v-model="modelFeature" label="Capability" :options="featureOptions" />
        <FilterSelect v-model="modelPrice" label="Price" :options="priceOptions" />
        <FilterSelect v-model="modelStatus" label="Status" :options="modelStatusOptions" />
      </div>

      <DataTable
        v-if="segment === 'providers'"
        :columns="providerColumns"
        :rows="providerView.paged.value"
        :row-key="(p) => p.id"
        caption="Providers"
        clickable
        :clearable="providersFiltering"
        :sort-key="providerView.sortKey.value"
        :sort-dir="providerView.sortDir.value"
        :empty-text="providersFiltering ? 'No providers match your filters.' : 'No providers yet. Add one to start serving models.'"
        @row-click="openProvider"
        @sort="providerView.toggleSort"
        @clear="clearProviderFilters"
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
        :rows="modelView.paged.value"
        :row-key="(m) => m.id"
        caption="Models"
        clickable
        :clearable="modelsFiltering"
        :sort-key="modelView.sortKey.value"
        :sort-dir="modelView.sortDir.value"
        :empty-text="modelsFiltering ? 'No models match your filters.' : 'No models yet. Add one so routes have something to send requests to.'"
        @row-click="openModel"
        @sort="modelView.toggleSort"
        @clear="clearModelFilters"
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
        <template #cell-features="{ row }">
          <span v-if="row.features.length" class="flex flex-wrap gap-1"><UiBadge v-for="f in row.features" :key="f" tone="neutral" :label="featureLabel(f)" /></span>
          <span v-else class="text-fg-3">Unknown</span>
        </template>
        <template #cell-context="{ row }">{{ row.contextWindow == null ? '—' : formatNumber(row.contextWindow) }}</template>
        <template #cell-price="{ row }">
          <span v-if="row.currentPrice">{{ formatUsd(row.currentPrice.inputPerMillionUsd) }} / {{ formatUsd(row.currentPrice.outputPerMillionUsd) }}</span>
          <span v-else class="text-fg-3">No price</span>
        </template>
        <template #cell-status="{ row }"><UiBadge :tone="row.isEnabled ? 'ok' : 'neutral'" :label="row.isEnabled ? 'Enabled' : 'Disabled'" /></template>
      </DataTable>

      <Pagination
        v-if="segment === 'providers' && providerView.sorted.value.length > providerView.pageSize"
        v-model:page="providerPage"
        :page-size="providerView.pageSize"
        :total="providerView.sorted.value.length"
        label="Provider pages"
      />
      <Pagination
        v-if="segment === 'models' && modelView.sorted.value.length > modelView.pageSize"
        v-model:page="modelPage"
        :page-size="modelView.pageSize"
        :total="modelView.sorted.value.length"
        label="Model pages"
      />

      <p v-if="segment === 'providers' && providers.items.length > 0" class="text-small text-fg-3">
        {{ plural(providers.items.reduce((n, p) => n + p.deploymentCount, 0), 'model') }} across {{ plural(providers.items.length, 'provider') }}.
        <template v-if="providersFiltering">Showing {{ filteredProviders.length }} of {{ providers.items.length }} providers.</template>
      </p>
      <p v-else-if="segment === 'models' && modelsFiltering && models.items.length > 0" class="text-small text-fg-3">
        Showing {{ filteredModels.length }} of {{ models.items.length }} models.
      </p>
    </div>
  </AsyncState>

  <ProviderFormDrawer v-model:open="providerDrawer" :provider="editingProvider" />
  <ModelFormDrawer v-model:open="modelDrawer" :model="editingModel" :providers="providers.items" />
</template>
