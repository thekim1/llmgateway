<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUsageStore } from '@/stores/usage'
import { useBudgetsStore } from '@/stores/budgets'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import BarTable from '@/components/BarTable.vue'
import BudgetMeter from '@/components/BudgetMeter.vue'
import { formatSek } from '@/utils/format'
const { t } = useI18n()
const usage = useUsageStore()
const budgets = useBudgetsStore()
const bars = computed(() => usage.summary?.rows.map(r => ({ key: r.key, label: r.label, value: r.costSek, valueText: formatSek(r.costSek) })) ?? [])
async function load(): Promise<void> { await Promise.all([usage.loadSummary({ groupBy: 'department' }), budgets.load()]) }
onMounted(load)
</script>
<template>
  <PageHeader :title="t('nav.overview')" :lead="t('footer.description')" />
  <AsyncState :loading="usage.summaryLoading || budgets.loading" :error="usage.summaryError || budgets.error" @retry="load">
    <div class="grid"><div class="card col-4"><p class="stat__label">{{ t('usage.cost') }}</p><p class="stat__value">{{ formatSek(usage.summary?.totalCostSek ?? 0) }}</p></div><div class="card col-4"><p class="stat__label">{{ t('usage.requests') }}</p><p class="stat__value">{{ usage.summary?.totalRequests ?? 0 }}</p></div><div class="card col-4"><p class="stat__label">{{ t('usage.errors') }}</p><p class="stat__value">{{ usage.summary?.totalErrors ?? 0 }}</p></div></div>
    <h2>{{ t('usage.summary') }}</h2><BarTable id="overview-costs" :caption="t('usage.summary')" :label-header="t('fields.departmentId')" :value-header="t('usage.cost')" :share-header="t('usage.cost')" :rows="bars" />
    <h2>{{ t('nav.budgets') }}</h2><p v-if="!budgets.items.length">{{ t('common.empty') }}</p><section v-for="budget in budgets.items" :key="budget.id"><h3>{{ budget.scopeName }}</h3><BudgetMeter :label="budget.scopeName" :spent="budget.spentSek" :limit="budget.limitSek" :thresholds="budget.alertThresholds" /></section>
  </AsyncState>
</template>
