<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { USAGE_GROUP_BY, type UsageGroupBy, type UsageRequest } from '@/api/types'
import { useUsageStore } from '@/stores/usage'
import { useNotify } from '@/composables/useForm'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import ScrollTable from '@/components/ScrollTable.vue'
import AppPagination from '@/components/AppPagination.vue'
import CodeBlock from '@/components/CodeBlock.vue'
import { formatSek, formatDateTime } from '@/utils/format'
const { t } = useI18n()
const usage = useUsageStore()
const notify = useNotify()
const from = ref(new Date(Date.now() - 30 * 86400000).toISOString().slice(0, 10))
const to = ref(new Date().toISOString().slice(0, 10))
const group = ref<UsageGroupBy>('department')
const page = ref(1)
const requestId = ref('')
const found = ref<UsageRequest | null>(null)
const lookupBusy = ref(false)
const dates = computed(() => ({ from: new Date(`${from.value}T00:00:00`).toISOString(), to: new Date(`${to.value}T23:59:59.999`).toISOString() }))
async function load(): Promise<void> {
  if (!from.value || !to.value || from.value > to.value) { notify.errorText(t('common.invalid')); return }
  await Promise.all([usage.loadSummary({ ...dates.value, groupBy: group.value }), usage.loadRequests({ ...dates.value, page: page.value })])
}
async function lookup(): Promise<void> {
  if (!requestId.value.trim() || lookupBusy.value) return
  lookupBusy.value = true
  found.value = null
  try { found.value = await usage.lookup(requestId.value.trim()) } catch (e) { notify.error(e) } finally { lookupBusy.value = false }
}
async function changePage(next: number): Promise<void> { page.value = next; await load() }
onMounted(load)
</script>
<template>
  <PageHeader :title="t('nav.usage')" :lead="t('footer.description')" />
  <form class="toolbar" @submit.prevent="page = 1; load()"><div class="field"><label for="usage-from">{{ t('usage.from') }}</label><input id="usage-from" v-model="from" type="date" class="input" required></div><div class="field"><label for="usage-to">{{ t('usage.to') }}</label><input id="usage-to" v-model="to" type="date" class="input" required></div><div class="field"><label for="usage-group">{{ t('usage.group') }}</label><select id="usage-group" v-model="group" class="select"><option v-for="option in USAGE_GROUP_BY" :key="option" :value="option">{{ t(`usage.groupOptions.${option}`) }}</option></select></div><button class="btn btn--primary" type="submit">{{ t('common.apply') }}</button></form>
  <a class="btn btn--secondary" :href="usage.exportCsvUrl(dates.from, dates.to)" download>{{ t('usage.export') }}</a>
  <h2>{{ t('usage.summary') }}</h2>
  <AsyncState :loading="usage.summaryLoading" :error="usage.summaryError" @retry="load">
    <ScrollTable :label="t('usage.summary')"><table class="table"><caption>{{ t('usage.summary') }}</caption><thead><tr><th scope="col">{{ t('common.name') }}</th><th scope="col">{{ t('usage.requests') }}</th><th scope="col">{{ t('usage.tokens') }}</th><th scope="col">{{ t('usage.cost') }}</th><th scope="col">{{ t('usage.errors') }}</th></tr></thead><tbody><tr v-for="row in usage.summary?.rows ?? []" :key="row.key"><th scope="row">{{ row.label }}</th><td>{{ row.requests }}</td><td>{{ row.inputTokens + row.outputTokens }}</td><td>{{ formatSek(row.costSek) }}</td><td>{{ row.errors }}</td></tr></tbody></table></ScrollTable>
  </AsyncState>
  <h2>{{ t('usage.requestList') }}</h2>
  <AsyncState :loading="usage.requestsLoading" :error="usage.requestsError" @retry="load">
    <ScrollTable :label="t('usage.requestList')"><table class="table"><caption>{{ t('usage.requestList') }}</caption><thead><tr><th scope="col">{{ t('usage.requestId') }}</th><th scope="col">{{ t('audit.timestamp') }}</th><th scope="col">{{ t('fields.name') }}</th><th scope="col">{{ t('usage.cost') }}</th><th scope="col">{{ t('common.status') }}</th></tr></thead><tbody><tr v-for="request in usage.requests.items" :key="request.requestId"><th scope="row"><button type="button" class="btn btn--ghost" @click="requestId = request.requestId; lookup()">{{ request.requestId }}</button></th><td>{{ formatDateTime(request.timestamp) }}</td><td>{{ request.keyName }}</td><td>{{ formatSek(request.costSek) }}</td><td>{{ request.statusCode }} · {{ request.outcome }}</td></tr></tbody></table></ScrollTable>
    <AppPagination :page="page" :page-size="50" :total="usage.requests.total" :label="t('usage.requestList')" @change="changePage" />
  </AsyncState>
  <h2>{{ t('usage.lookup') }}</h2><form class="form" @submit.prevent="lookup"><label for="request-id">{{ t('usage.requestId') }}</label><p id="lookup-help" class="help">{{ t('usage.lookupHelp') }}</p><input id="request-id" v-model="requestId" class="input" maxlength="64" aria-describedby="lookup-help" required><button class="btn btn--primary" type="submit" :disabled="lookupBusy">{{ t('usage.lookup') }}</button></form>
  <CodeBlock v-if="found" :code="JSON.stringify(found, null, 2)" :label="t('usage.requestId') + ': ' + found.requestId" />
</template>
