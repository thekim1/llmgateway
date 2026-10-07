<script setup lang="ts">
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useCatalogStore } from '@/stores/catalog'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import ScrollTable from '@/components/ScrollTable.vue'
import ResidencyBadge from '@/components/ResidencyBadge.vue'
import { formatSek } from '@/utils/format'
const { t } = useI18n()
const catalog = useCatalogStore()
onMounted(() => { void catalog.load() })
</script>
<template>
  <PageHeader :title="t('nav.catalog')" :lead="t('catalog.help')" />
  <AsyncState :loading="catalog.loading" :error="catalog.error" @retry="catalog.load(true)">
    <ScrollTable :label="t('nav.routes')"><table class="table"><caption>{{ t('nav.routes') }}</caption><thead><tr><th scope="col">{{ t('common.name') }}</th><th scope="col">{{ t('residency.label') }}</th><th scope="col">{{ t('fields.capabilities') }}</th><th scope="col">{{ t('catalog.input') }}</th><th scope="col">{{ t('catalog.output') }}</th></tr></thead><tbody><tr v-for="route in catalog.catalog?.routes ?? []" :key="route.name"><th scope="row">{{ route.name }}<p>{{ route.description }}</p></th><td><ResidencyBadge v-for="residency in route.residencies" :key="residency" :residency="residency" /></td><td>{{ route.capabilities.join(', ') }}</td><td>{{ route.inputSekPerMillion === null ? '—' : formatSek(route.inputSekPerMillion) }}</td><td>{{ route.outputSekPerMillion === null ? '—' : formatSek(route.outputSekPerMillion) }}</td></tr></tbody></table></ScrollTable>
    <h2>{{ t('nav.models') }}</h2><ScrollTable :label="t('nav.models')"><table class="table"><caption>{{ t('nav.models') }}</caption><thead><tr><th scope="col">{{ t('common.name') }}</th><th scope="col">{{ t('residency.label') }}</th><th scope="col">{{ t('fields.kind') }}</th><th scope="col">{{ t('catalog.input') }}</th><th scope="col">{{ t('catalog.output') }}</th></tr></thead><tbody><tr v-for="model in catalog.catalog?.models ?? []" :key="model.name"><th scope="row">{{ model.name }}</th><td><ResidencyBadge :residency="model.residency" /></td><td>{{ model.kind }}</td><td>{{ model.inputSekPerMillion === null ? '—' : formatSek(model.inputSekPerMillion) }}</td><td>{{ model.outputSekPerMillion === null ? '—' : formatSek(model.outputSekPerMillion) }}</td></tr></tbody></table></ScrollTable>
  </AsyncState>
</template>
