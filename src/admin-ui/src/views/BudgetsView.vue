<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useBudgetsStore } from '@/stores/budgets'
import { useNotify } from '@/composables/useForm'
import PageHeader from '@/components/PageHeader.vue'
import ResourcePanel from '@/components/ResourcePanel.vue'
import AsyncState from '@/components/AsyncState.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
const { t } = useI18n()
const budgets = useBudgetsStore()
const notify = useNotify()
const acknowledgeId = ref<string | null>(null)
const busy = ref(false)
const load = () => budgets.loadAlerts(false)
async function acknowledge(): Promise<void> {
  if (!acknowledgeId.value || busy.value) return
  busy.value = true
  try { await budgets.acknowledge(acknowledgeId.value); acknowledgeId.value = null; await load(); notify.success(t('common.saved')) } catch (e) { notify.error(e) } finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <PageHeader :title="t('nav.budgets')" /><ResourcePanel resource="budgets" :title="t('nav.budgets')" />
  <h2>{{ t('budgets.alerts') }}</h2><AsyncState :loading="budgets.alertsLoading" :error="budgets.alertsError" @retry="load"><p v-if="!budgets.alerts.length">{{ t('budgets.noAlerts') }}</p><div v-for="alert in budgets.alerts" :key="alert.id" class="notice notice--warning"><p>{{ alert.scopeName }} · {{ alert.thresholdPercent }} % · {{ alert.spentSek }} / {{ alert.limitSek }} SEK</p><button type="button" class="btn btn--secondary" @click="acknowledgeId = alert.id">{{ t('budgets.acknowledge') }} · {{ alert.scopeName }}</button></div></AsyncState>
  <ConfirmDialog :open="!!acknowledgeId" :title="t('budgets.acknowledge')" :description="t('common.confirmBody')" :confirm-label="t('budgets.acknowledge')" :busy="busy" tone="primary" @update:open="!$event && (acknowledgeId = null)" @confirm="acknowledge" />
</template>
