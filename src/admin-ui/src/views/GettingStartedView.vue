<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useCatalogStore } from '@/stores/catalog'
import { GATEWAY_ERROR_CODES } from '@/api/types'
import { snippets } from '@/utils/snippets'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import CodeBlock from '@/components/CodeBlock.vue'
const { t } = useI18n()
const catalog = useCatalogStore()
const model = ref('')
const examples = computed(() => snippets(catalog.catalog?.gatewayBaseUrl ?? '', model.value))
onMounted(async () => { const result = await catalog.load(); model.value = result?.routes.find(r => r.kind === 'Chat')?.name ?? '' })
</script>
<template>
  <PageHeader :title="t('nav.gettingStarted')" :lead="t('portal.help')" /><p class="notice notice--warning">{{ t('portal.privacy') }}</p>
  <AsyncState :loading="catalog.loading" :error="catalog.error" @retry="catalog.load(true)">
    <h2>{{ t('portal.baseUrl') }}</h2><CodeBlock :code="catalog.catalog?.gatewayBaseUrl ?? ''" :label="t('portal.baseUrl')" />
    <label for="snippet-model">{{ t('portal.model') }}</label><select id="snippet-model" v-model="model" class="select"><option v-for="route in catalog.catalog?.routes ?? []" :key="route.name" :value="route.name">{{ route.name }}</option></select>
    <h2>{{ t('nav.gettingStarted') }} · API</h2><p v-if="!model">{{ t('common.empty') }}</p><template v-else><CodeBlock v-for="example in examples" :key="example.label" :code="example.code" :label="example.label" :language="example.language" /></template>
    <p><a :href="`${catalog.catalog?.gatewayBaseUrl}/openapi/v1.json`">{{ t('portal.openapi') }}</a></p><p><a :href="`${catalog.catalog?.gatewayBaseUrl}/scalar`">{{ t('portal.reference') }}</a></p>
  </AsyncState>
  <h2>{{ t('portal.errors') }}</h2><ul><li v-for="code in GATEWAY_ERROR_CODES" :key="code"><code>{{ code }}</code></li></ul><RouterLink :to="{ name: 'usage' }">{{ t('portal.requestLookup') }}</RouterLink>
  <h2>{{ t('portal.glossary') }}</h2><dl class="details"><dt>{{ t('portal.virtualKey') }}</dt><dd>{{ t('portal.virtualKeyHelp') }}</dd><dt>{{ t('portal.fallback') }}</dt><dd>{{ t('portal.fallbackHelp') }}</dd><dt>{{ t('portal.budget') }}</dt><dd>{{ t('portal.budgetHelp') }}</dd></dl>
</template>
