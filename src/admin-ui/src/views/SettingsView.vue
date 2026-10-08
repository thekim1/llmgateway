<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useBudgetsStore } from '@/stores/budgets'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { parseDecimal } from '@/utils/validation'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

const budgets = useBudgetsStore()
const ui = useUiStore()

const rate = ref('')
const rateError = ref<string | null>(null)
const parsed = ref(0)
const confirmOpen = ref(false)
const loading = ref(true)

async function load(): Promise<void> {
  loading.value = true
  const result = await budgets.loadExchangeRate()
  if (result) rate.value = String(result.sekPerUnit).replace('.', ',')
  loading.value = false
}

function review(): void {
  const value = parseDecimal(rate.value)
  if (value === null || Number.isNaN(value) || value <= 0) {
    rateError.value = 'Enter a rate above 0, for example 10,50.'
    return
  }
  rateError.value = null
  parsed.value = value
  confirmOpen.value = true
}

async function save(): Promise<void> {
  await budgets.updateExchangeRate(parsed.value)
  ui.notify('Exchange rate saved')
}

onMounted(load)
</script>

<template>
  <PageHeader title="Settings" description="Gateway-wide settings. Theme and display options are in the top bar." />

  <UiCard title="Exchange rate" meta="USD to SEK" heading-id="rate-h" class="max-w-xl">
    <AsyncState :loading="loading" :error="budgets.exchangeRateError" :empty="false" @retry="load">
      <p class="mb-4 text-fg-2">
        Model prices are set in USD per 1M tokens. Costs and budgets are shown in SEK using this rate.
        <template v-if="budgets.exchangeRate">Current rate in effect since {{ formatDateTime(budgets.exchangeRate.effectiveFrom) }}.</template>
      </p>
      <form class="flex flex-col items-start gap-4" novalidate @submit.prevent="review">
        <TextField v-model="rate" label="SEK per 1 USD" inputmode="decimal" :error="rateError" hint="Applies to new usage from the moment you save." />
        <UiButton type="submit" variant="primary">Review change</UiButton>
      </form>
    </AsyncState>
  </UiCard>

  <ConfirmDialog
    v-model:open="confirmOpen"
    title="Change exchange rate?"
    :consequence="`1 USD will count as ${rate} kr for new usage. Recorded costs are not recalculated.`"
    confirm-label="Save rate"
    :action="save"
  />
</template>
