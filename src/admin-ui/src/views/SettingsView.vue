<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import { useBudgetsStore } from '@/stores/budgets'
import { useForm, useNotify } from '@/composables/useForm'
import PageHeader from '@/components/PageHeader.vue'
import ThemePicker from '@/components/ThemePicker.vue'
import LocalePicker from '@/components/LocalePicker.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ErrorSummary from '@/components/form/ErrorSummary.vue'
import FormField from '@/components/form/FormField.vue'
import AsyncState from '@/components/AsyncState.vue'
const { t } = useI18n()
const auth = useAuthStore()
const budgets = useBudgetsStore()
const form = useForm('exchange', ['sekPerUnit'] as const)
const { summary, busy } = form
const notify = useNotify()
const rate = ref(10)
const confirm = ref(false)
function validate(): void { if (!Number.isFinite(Number(rate.value)) || Number(rate.value) <= 0) form.setError('sekPerUnit', t('common.invalid')) }
async function review(): Promise<void> { await form.submit(validate, async () => { confirm.value = true }) }
async function save(): Promise<void> {
  if (busy.value) return
  const ok = await form.submit(validate, async () => { await budgets.updateExchangeRate(Number(rate.value)); confirm.value = false; notify.success(t('common.saved')) })
  if (!ok) confirm.value = false
}
async function load(): Promise<void> { const result = await budgets.loadExchangeRate(); if (result) rate.value = result.sekPerUnit }
onMounted(() => { if (auth.isGatewayAdmin) void load() })
</script>
<template>
  <PageHeader :title="t('nav.settings')" :lead="t('settings.help')" />
  <h2>{{ t('theme.label') }}</h2><ThemePicker id="settings-theme" /><h2>{{ t('locale.label') }}</h2><LocalePicker id="settings-locale" />
  <h2>{{ t('settings.accessibility') }}</h2><p>{{ t('settings.accessibilityHelp') }}</p>
  <template v-if="auth.isGatewayAdmin"><h2>{{ t('settings.exchange') }}</h2><AsyncState :loading="false" :error="budgets.exchangeRateError" @retry="load"><form class="form" novalidate @submit.prevent="review"><ErrorSummary :id="form.summaryId" :items="summary" /><FormField :id="form.fieldId('sekPerUnit')" :label="t('settings.exchange')" :help="t('common.confirmBody')" :error="form.errors.sekPerUnit" required><template #default="{ id, describedBy, invalid }"><input :id="id" v-model="rate" type="number" min="0.000001" step="any" class="input" :aria-describedby="describedBy" :aria-invalid="invalid || undefined"></template></FormField><button class="btn btn--primary" type="submit" :disabled="busy">{{ t('common.review') }}</button></form></AsyncState></template>
  <ConfirmDialog v-model:open="confirm" :title="t('settings.exchange')" :description="`${t('common.confirmBody')} 1 USD = ${rate} SEK`" :confirm-label="t('common.save')" :acknowledge-label="t('common.acknowledge')" tone="primary" :busy="busy" @confirm="save" />
</template>
