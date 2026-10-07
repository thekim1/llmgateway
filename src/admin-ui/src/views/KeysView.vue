<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { KEY_STATUSES, type KeyStatus } from '@/api/types'
import { useKeysStore } from '@/stores/keys'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import ScrollTable from '@/components/ScrollTable.vue'
import KeyStatusBadge from '@/components/KeyStatusBadge.vue'
const { t } = useI18n()
const keys = useKeysStore()
const status = ref<KeyStatus | ''>('')
const load = () => keys.load(status.value ? { status: status.value } : {})
onMounted(load)
</script>
<template>
  <PageHeader :title="t('nav.keys')" :lead="t('keys.help')"><template #actions><RouterLink class="btn btn--primary" :to="{ name: 'key-create' }">{{ t('keys.create.title') }}</RouterLink></template></PageHeader>
  <form class="toolbar" @submit.prevent="load"><div class="field"><label for="key-status">{{ t('common.status') }}</label><select id="key-status" v-model="status" class="select"><option value="">{{ t('common.all') }}</option><option v-for="option in KEY_STATUSES" :key="option" :value="option">{{ t(`enums.keyStatus.${option}`) }}</option></select></div><button class="btn btn--secondary" type="submit">{{ t('common.apply') }}</button></form>
  <AsyncState :loading="keys.loading" :error="keys.error" @retry="load">
    <p v-if="!keys.items.length">{{ t('common.empty') }}. {{ t('common.emptyHelp') }}</p>
    <ScrollTable v-else :label="t('nav.keys')"><table class="table"><caption>{{ t('nav.keys') }}</caption><thead><tr><th scope="col">{{ t('fields.name') }}</th><th scope="col">{{ t('keys.prefix') }}</th><th scope="col">{{ t('fields.teamId') }}</th><th scope="col">{{ t('common.status') }}</th></tr></thead><tbody><tr v-for="key in keys.items" :key="key.id"><th scope="row"><RouterLink :to="{ name: 'key-detail', params: { id: key.id } }">{{ key.name }}</RouterLink></th><td class="mono">{{ key.prefix }}</td><td>{{ key.departmentName }} · {{ key.teamName }}</td><td><KeyStatusBadge :status="key.status" /></td></tr></tbody></table></ScrollTable>
  </AsyncState>
</template>
