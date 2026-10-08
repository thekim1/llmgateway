<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import { BUDGET_PERIODS, type Budget, type BudgetPeriod } from '@/api/types'
import { PERIOD_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { formatPercent, formatSek } from '@/utils/format'
import { parseDecimal } from '@/utils/validation'
import UiButton from '@/components/ui/UiButton.vue'
import UiMeter from '@/components/ui/UiMeter.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'

const props = defineProps<{ keyId: string; editable: boolean }>()

const budgets = ref<Budget[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const period = ref<BudgetPeriod>('Monthly')
const limit = ref('')
const limitError = ref<string | null>(null)
const busy = ref(false)

const periodRank = (p: BudgetPeriod): number => BUDGET_PERIODS.indexOf(p)
const sorted = computed(() => [...budgets.value].sort((a, b) => periodRank(a.period) - periodRank(b.period)))
// One budget per period: only offer periods the key does not have yet.
const freePeriods = computed(() => BUDGET_PERIODS.filter((p) => !budgets.value.some((b) => b.period === p)))
const periodOptions = computed(() => freePeriods.value.map((p) => ({ value: p, label: PERIOD_LABEL[p] })))

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    budgets.value = await api.budgets.list({ scope: 'VirtualKey', scopeId: props.keyId })
    if (!freePeriods.value.includes(period.value)) period.value = freePeriods.value[0] ?? 'Monthly'
  } catch (e) {
    error.value = problemMessage(e)
  } finally {
    loading.value = false
  }
}

async function add(): Promise<void> {
  const value = parseDecimal(limit.value)
  if (value === null || Number.isNaN(value) || value <= 0) {
    limitError.value = 'Enter a limit above 0 kr, for example 500.'
    return
  }
  limitError.value = null
  busy.value = true
  error.value = null
  try {
    await api.budgets.create({ scope: 'VirtualKey', scopeId: props.keyId, limitSek: value, period: period.value, alertThresholds: [50, 80, 100], isActive: true })
    limit.value = ''
    await load()
  } catch (e) {
    error.value = problemMessage(e)
  } finally {
    busy.value = false
  }
}

async function remove(budget: Budget): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await api.budgets.remove(budget.id)
    await load()
  } catch (e) {
    error.value = problemMessage(e)
  } finally {
    busy.value = false
  }
}

watch(() => props.keyId, load, { immediate: true })
</script>

<template>
  <div>
    <p v-if="loading && budgets.length === 0" class="text-small text-fg-3">Loading budgets…</p>
    <p v-else-if="sorted.length === 0" class="text-small text-fg-3">No budget. The key is only limited by its team’s and department’s budgets.</p>
    <ul v-else class="m-0 flex list-none flex-col gap-3 p-0" data-testid="key-budgets">
      <li v-for="b in sorted" :key="b.id" class="flex flex-col gap-1">
        <div class="flex items-center justify-between gap-2 text-small">
          <span class="font-medium">{{ PERIOD_LABEL[b.period] }}<span v-if="!b.isActive" class="text-fg-3"> · paused</span></span>
          <span class="tabular">{{ formatSek(b.spentSek) }} <span class="text-fg-3">/ {{ formatSek(b.limitSek) }}</span></span>
        </div>
        <div class="flex items-center gap-2">
          <UiMeter :value="b.percentUsed" :label="`${PERIOD_LABEL[b.period]} budget used`" :ticks="b.alertThresholds" thin class="flex-1" />
          <span class="tabular w-12 text-right text-small">{{ formatPercent(b.percentUsed) }}</span>
          <UiButton v-if="editable" size="sm" icon="delete" :disabled="busy" :aria-label="`Delete ${PERIOD_LABEL[b.period]} budget`" @click="remove(b)" />
        </div>
      </li>
    </ul>
    <form v-if="editable && freePeriods.length > 0" class="mt-4 flex flex-wrap items-end gap-2" novalidate @submit.prevent="add">
      <SelectField v-model="period" label="Period" :options="periodOptions" class="w-36" />
      <TextField v-model="limit" label="Limit (kr)" inputmode="decimal" :error="limitError" class="w-36" />
      <UiButton type="submit" size="sm" icon="add" :loading="busy">Add budget</UiButton>
    </form>
    <p v-if="error" class="mt-2 text-small text-danger" role="alert">{{ error }}</p>
  </div>
</template>
