<script setup lang="ts">
import { computed, onMounted } from 'vue'
import type { ProviderCapability } from '@/api/types'
import { useCatalogStore } from '@/stores/catalog'
import { CAPABILITY_LABEL, KIND_LABEL } from '@/utils/labels'
import { formatUsdCost } from '@/utils/format'
import CodeBlock from '@/components/keys/CodeBlock.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'

const catalog = useCatalogStore()
onMounted(() => {
  void catalog.load()
})

/** The website's guide for application developers (it replaced the Getting started page). */
const guideUrl = 'https://thekim1.github.io/llmgateway/start/'
const baseUrl = computed(() => (catalog.catalog?.gatewayBaseUrl ?? '').replace(/\/+$/, '') + '/v1')
const routes = computed(() => catalog.catalog?.routes ?? [])
const models = computed(() => catalog.catalog?.models ?? [])

const routeColumns: Column[] = [
  { key: 'name', label: 'Alias' },
  { key: 'residency', label: 'Residency' },
  { key: 'capabilities', label: 'Capabilities' },
  { key: 'input', label: 'Input, USD per 1M', align: 'right' },
  { key: 'output', label: 'Output, USD per 1M', align: 'right' },
]
const modelColumns: Column[] = [
  { key: 'name', label: 'Model' },
  { key: 'residency', label: 'Residency' },
  { key: 'kind', label: 'Kind' },
  { key: 'input', label: 'Input, USD per 1M', align: 'right' },
  { key: 'output', label: 'Output, USD per 1M', align: 'right' },
]

const capabilities = (list: ProviderCapability[]): string => list.map((c) => CAPABILITY_LABEL[c]).join(', ') || '—'
const price = (value: number | null): string => (value === null ? '—' : formatUsdCost(value))
</script>

<template>
  <PageHeader title="Catalogue" description="Model aliases and models you can use. Prices are estimates in USD per million tokens." />
  <AsyncState :loading="catalog.loading" :error="catalog.error" :empty="!catalog.catalog" empty-text="The catalogue is empty." @retry="catalog.load(true)">
    <div class="flex flex-col gap-8">
      <section aria-labelledby="catalog-connect" class="flex flex-col gap-3">
        <h2 id="catalog-connect" class="sr-only">Connect an application</h2>
        <CodeBlock :code="baseUrl" label="Gateway base URL" />
        <p class="text-small text-fg-2">
          Use the base URL and a key from your team with any OpenAI or Anthropic SDK.
          <a class="text-accent-ink underline" :href="guideUrl" target="_blank" rel="noopener">Getting started guide<AppIcon name="open_in_new" :size="16" class="ml-1 align-text-bottom" /><span class="sr-only"> (opens in a new tab)</span></a>
        </p>
      </section>
      <section aria-labelledby="catalog-routes" class="flex flex-col gap-3">
        <h2 id="catalog-routes" class="text-heading">Model aliases</h2>
        <DataTable :columns="routeColumns" :rows="routes" :row-key="(r) => r.name" caption="Model aliases" empty-text="No aliases available.">
          <template #cell-name="{ row }">
            <span class="block font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.description" class="block text-caption text-fg-3">{{ row.description }}</span>
            <span class="block text-caption text-fg-3">{{ KIND_LABEL[row.kind] }}</span>
          </template>
          <template #cell-residency="{ row }">
            <span class="flex flex-wrap gap-x-2"><ResidencyLabel v-for="r in row.residencies" :key="r" :residency="r" /></span>
          </template>
          <template #cell-capabilities="{ row }"><span class="text-small text-fg-2">{{ capabilities(row.capabilities) }}</span></template>
          <template #cell-input="{ row }">{{ price(row.inputUsdPerMillion) }}</template>
          <template #cell-output="{ row }">{{ price(row.outputUsdPerMillion) }}</template>
        </DataTable>
      </section>

      <section aria-labelledby="catalog-models" class="flex flex-col gap-3">
        <h2 id="catalog-models" class="text-heading">Models</h2>
        <DataTable :columns="modelColumns" :rows="models" :row-key="(m) => m.name" caption="Models" empty-text="No models available.">
          <template #cell-name="{ row }"><span class="font-mono font-medium">{{ row.name }}</span></template>
          <template #cell-residency="{ row }"><ResidencyLabel :residency="row.residency" /></template>
          <template #cell-kind="{ row }">{{ KIND_LABEL[row.kind] }}</template>
          <template #cell-input="{ row }">{{ price(row.inputUsdPerMillion) }}</template>
          <template #cell-output="{ row }">{{ price(row.outputUsdPerMillion) }}</template>
        </DataTable>
      </section>
    </div>
  </AsyncState>
</template>
