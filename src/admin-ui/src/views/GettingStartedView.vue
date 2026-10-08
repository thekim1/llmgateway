<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { GATEWAY_ERROR_CODES } from '@/api/types'
import { useCatalogStore } from '@/stores/catalog'
import { snippets } from '@/utils/snippets'
import CodeBlock from '@/components/keys/CodeBlock.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import DetailSection from '@/components/ui/DetailSection.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiCard from '@/components/ui/UiCard.vue'

const catalog = useCatalogStore()
const model = ref('')

const baseUrl = computed(() => catalog.catalog?.gatewayBaseUrl ?? '')
const aliasOptions = computed(() => (catalog.catalog?.routes ?? []).map((r) => ({ value: r.name, label: r.name })))
const examples = computed(() => snippets(baseUrl.value, model.value))

onMounted(async () => {
  const result = await catalog.load()
  model.value = result?.routes.find((r) => r.kind === 'Chat')?.name ?? result?.routes[0]?.name ?? ''
})
</script>

<template>
  <PageHeader title="Getting started" description="Connect an application to the gateway with a key from your team." />
  <div class="flex flex-col gap-8">
    <p class="flex items-start gap-2 rounded-control bg-warn-soft p-3 text-small text-warn">
      <AppIcon name="warning" :size="18" filled class="mt-px" />
      Prompts and responses are never saved. Don’t send confidential information to external providers without an approved assessment.
    </p>

    <AsyncState :loading="catalog.loading" :error="catalog.error" :empty="!catalog.catalog" empty-text="The catalogue isn’t available yet." @retry="catalog.load(true)">
      <div class="flex flex-col gap-6">
        <UiCard title="Connect" padding="lg">
          <div class="flex flex-col gap-4">
            <CodeBlock :code="baseUrl" label="Gateway base URL" />
            <SelectField v-model="model" label="Model alias" :options="aliasOptions" hint="Examples read the key from UME_API_KEY. Never store a key in code." />
          </div>
        </UiCard>

        <section aria-labelledby="gs-examples" class="flex flex-col gap-3">
          <h2 id="gs-examples" class="text-heading">Examples</h2>
          <p v-if="!model" class="text-fg-2">Choose a model alias to see examples.</p>
          <CodeBlock v-for="example in examples" v-else :key="example.label" :code="example.code" :label="example.label" />
          <p class="flex flex-wrap gap-x-6 gap-y-1 text-small">
            <a class="text-accent-ink underline" :href="`${baseUrl}/openapi/v1.json`">OpenAPI document<AppIcon name="open_in_new" :size="16" class="ml-1 align-text-bottom" /></a>
            <a class="text-accent-ink underline" :href="`${baseUrl}/scalar`">API reference<AppIcon name="open_in_new" :size="16" class="ml-1 align-text-bottom" /></a>
          </p>
        </section>
      </div>
    </AsyncState>

    <UiCard title="Common errors" padding="lg">
      <ul class="m-0 flex list-none flex-wrap gap-2 p-0">
        <li v-for="code in GATEWAY_ERROR_CODES" :key="code" class="rounded-chip bg-sunken px-2 py-1 font-mono text-small">{{ code }}</li>
      </ul>
      <p class="mt-4 text-small text-fg-2">
        To investigate a failed call, find it by request ID in
        <RouterLink :to="{ name: 'usage' }" class="text-accent-ink underline">Usage</RouterLink>.
      </p>
    </UiCard>

    <section aria-labelledby="gs-glossary">
      <h2 id="gs-glossary" class="mb-3 text-heading">Glossary</h2>
      <div class="material rounded-card px-6 py-4">
        <DetailSection title="Terms" class="!mb-0">
          <DetailRow label="Key">A secret that identifies an application and controls its access, limits and budget.</DetailRow>
          <DetailRow label="Fallback">The next provider tried when a call fails before the response starts.</DetailRow>
          <DetailRow label="Budget">The most an owner may spend during a calendar period.</DetailRow>
        </DetailSection>
      </div>
    </section>
  </div>
</template>
