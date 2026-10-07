<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import type { OpsProviderHealth } from '@/api/types'
import { useOpsStore } from '@/stores/ops'
import { useNotify } from '@/composables/useForm'
import PageHeader from '@/components/PageHeader.vue'
import AsyncState from '@/components/AsyncState.vue'
import HealthBadge from '@/components/HealthBadge.vue'
import ScrollTable from '@/components/ScrollTable.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import CodeBlock from '@/components/CodeBlock.vue'
const { t } = useI18n()
const ops = useOpsStore()
const notify = useNotify()
const config = ref('')
const confirmOpen = ref(false)
const actionTitle = ref('')
const busy = ref(false)
let action: (() => Promise<unknown>) | null = null
function review(title: string, work: () => Promise<unknown>): void { actionTitle.value = title; action = work; confirmOpen.value = true }
function reviewDrain(provider: OpsProviderHealth): void { review(provider.name, () => api.providers.drain(provider.id, !provider.isDrained)) }
function reviewCircuit(provider: OpsProviderHealth): void { review(provider.name, () => ops.setCircuit(provider.id, provider.circuitState === 'Open' ? 'Closed' : 'Open')) }
async function execute(): Promise<void> {
  if (!action || busy.value) return
  busy.value = true
  try { await action(); confirmOpen.value = false; notify.success(t('common.saved')); await ops.loadHealth() }
  catch (e) { notify.error(e) } finally { busy.value = false }
}
async function exportConfig(): Promise<void> {
  try { config.value = JSON.stringify(await ops.exportConfig(), null, 2) } catch (e) { notify.error(e) }
}
function importConfig(): void {
  let value: unknown
  try { value = JSON.parse(config.value) } catch { notify.errorText(t('common.invalid')); return }
  if (!value || typeof value !== 'object' || Array.isArray(value)) { notify.errorText(t('common.invalid')); return }
  const document = Object.fromEntries(Object.entries(value))
  review(t('ops.import'), () => ops.importConfig(document))
}
onMounted(() => { void ops.loadHealth() })
</script>
<template>
  <PageHeader :title="t('nav.ops')" :lead="t('ops.help')"><template #actions><button class="btn btn--secondary" type="button" @click="ops.loadHealth">{{ t('common.refresh') }}</button></template></PageHeader>
  <AsyncState :loading="ops.healthLoading" :error="ops.healthError" @retry="ops.loadHealth">
    <h2>{{ t('ops.components') }}</h2><ul><li v-for="component in ops.health?.components ?? []" :key="component.name">{{ component.name }} · <HealthBadge :status="component.status" /> · {{ component.description }}</li></ul>
    <h2>{{ t('ops.versions') }}</h2><dl class="details"><template v-for="(value, name) in ops.health?.versions ?? {}" :key="name"><dt>{{ name }}</dt><dd>{{ value ?? t('ops.unavailable') }}</dd></template></dl>
    <h2>{{ t('ops.usageWriter') }}</h2>
    <dl v-if="ops.health?.usageWriter" class="details">
      <dt>{{ t('ops.queueDepth') }}</dt><dd>{{ ops.health.usageWriter.queueDepth }} / {{ ops.health.usageWriter.capacity }}</dd>
      <dt>{{ t('ops.inFlight') }}</dt><dd>{{ ops.health.usageWriter.inFlightRecords }}</dd>
      <dt>{{ t('ops.lastWrite') }}</dt><dd>{{ ops.health.usageWriter.lastWriteAt ?? t('ops.noWrites') }}</dd>
      <dt>{{ t('ops.writeFailures') }}</dt><dd>{{ ops.health.usageWriter.consecutiveFailures }}</dd>
    </dl><p v-else class="notice notice--warning">{{ t('ops.unavailable') }}</p>
    <h2>{{ t('nav.providers') }}</h2><ScrollTable :label="t('nav.providers')"><table class="table"><caption>{{ t('nav.providers') }}</caption><thead><tr><th scope="col">{{ t('common.name') }}</th><th scope="col">{{ t('ops.circuit') }}</th><th scope="col">{{ t('usage.requests') }} (24 h)</th><th scope="col">p50 / p95 (ms)</th><th scope="col">{{ t('common.actions') }}</th></tr></thead><tbody><tr v-for="provider in ops.health?.providers ?? []" :key="provider.id"><th scope="row">{{ provider.name }}</th><td>{{ provider.circuitState }} · {{ provider.isDrained ? t('ops.drain') : t('ops.resume') }}</td><td>{{ provider.requests24h }}</td><td>{{ provider.p50LatencyMs ?? '—' }} / {{ provider.p95LatencyMs ?? '—' }}</td><td><div class="actions"><button class="btn btn--secondary" type="button" @click="reviewDrain(provider)">{{ t(provider.isDrained ? 'ops.resume' : 'ops.drain') }} {{ provider.name }}</button><button class="btn btn--secondary" type="button" @click="reviewCircuit(provider)">{{ t(provider.circuitState === 'Open' ? 'ops.close' : 'ops.open') }} {{ provider.name }}</button></div></td></tr></tbody></table></ScrollTable>
  </AsyncState>
  <h2>{{ t('ops.config') }}</h2><div class="actions"><button class="btn btn--secondary" type="button" @click="review(t('ops.invalidate'), ops.invalidateKeyCache)">{{ t('ops.invalidate') }}</button><button class="btn btn--secondary" type="button" @click="exportConfig">{{ t('ops.export') }}</button></div>
  <form class="form" @submit.prevent="importConfig"><label for="ops-config">{{ t('ops.config') }}</label><p id="ops-config-help" class="help">{{ t('ops.configHelp') }}</p><textarea id="ops-config" v-model="config" class="textarea mono" rows="12" aria-describedby="ops-config-help" required></textarea><button class="btn btn--primary" type="submit">{{ t('ops.import') }}</button></form>
  <CodeBlock v-if="config" :code="config" :label="t('ops.export')" />
  <ConfirmDialog v-model:open="confirmOpen" :title="actionTitle" :description="t('ops.confirm')" :confirm-label="t('common.confirm')" :acknowledge-label="t('common.acknowledge')" :busy="busy" @confirm="execute" />
</template>
