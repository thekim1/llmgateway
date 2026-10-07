<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useOpsStore } from '@/stores/ops'
import { formatDateTime } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import ScrollTable from '@/components/ScrollTable.vue'
import AppPagination from '@/components/AppPagination.vue'
const { t } = useI18n()
const ops = useOpsStore()
const page = ref(1)
const entityType = ref('')
const load = () => ops.loadAudit({ page: page.value, pageSize: 50, entityType: entityType.value || undefined })
async function changePage(next: number): Promise<void> { page.value = next; await load() }
onMounted(load)
</script>
<template>
  <PageHeader :title="t('nav.audit')" :lead="t('audit.help')" /><form class="toolbar" @submit.prevent="page = 1; load()"><div class="field"><label for="audit-entity">{{ t('audit.entity') }}</label><select id="audit-entity" v-model="entityType" class="select"><option value="">{{ t('common.all') }}</option><option v-for="type in ['Department', 'Team', 'VirtualKey', 'Provider', 'Model', 'Route', 'Budget', 'Config']" :key="type" :value="type">{{ type }}</option></select></div><button class="btn btn--secondary" type="submit">{{ t('common.apply') }}</button></form>
  <AsyncState :loading="ops.auditLoading" :error="ops.auditError" @retry="load"><ScrollTable :label="t('nav.audit')"><table class="table"><caption>{{ t('nav.audit') }}</caption><thead><tr><th scope="col">{{ t('audit.timestamp') }}</th><th scope="col">{{ t('audit.actor') }}</th><th scope="col">{{ t('audit.action') }}</th><th scope="col">{{ t('audit.entity') }}</th><th scope="col">{{ t('common.details') }}</th></tr></thead><tbody><tr v-for="entry in ops.audit.items" :key="entry.id"><th scope="row">{{ formatDateTime(entry.timestamp) }}</th><td>{{ entry.actor }}</td><td>{{ entry.action }}</td><td>{{ entry.entityType }} · {{ entry.entityId }}</td><td><details v-if="entry.details"><summary>{{ t('common.details') }} {{ entry.id }}</summary><pre class="mono">{{ typeof entry.details === 'string' ? entry.details : JSON.stringify(entry.details, null, 2) }}</pre></details></td></tr></tbody></table></ScrollTable><AppPagination :page="page" :page-size="50" :total="ops.audit.total" :label="t('nav.audit')" @change="changePage" /></AsyncState>
</template>
