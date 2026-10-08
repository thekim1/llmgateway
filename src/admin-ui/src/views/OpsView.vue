<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { OpsProviderHealth } from '@/api/types'
import { useOpsStore } from '@/stores/ops'
import { useUiStore } from '@/stores/ui'
import { HEALTH_STATUS } from '@/utils/labels'
import { formatDateTime, formatNumber } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import ConfigDialog from '@/components/ops/ConfigDialog.vue'
import ProviderCircuitTable from '@/components/ops/ProviderCircuitTable.vue'

const ops = useOpsStore()
const ui = useUiStore()

const circuitTarget = ref<OpsProviderHealth | null>(null)
const circuitOpen = ref(false)
const cacheOpen = ref(false)
const configOpen = ref(false)

const health = computed(() => ops.health)
const writer = computed(() => health.value?.usageWriter ?? null)
const isOpening = computed(() => circuitTarget.value?.circuitState !== 'Open')

function reviewCircuit(provider: OpsProviderHealth): void {
  circuitTarget.value = provider
  circuitOpen.value = true
}

async function toggleCircuit(): Promise<void> {
  const provider = circuitTarget.value
  if (!provider) return
  const next = provider.circuitState === 'Open' ? 'Closed' : 'Open'
  await ops.setCircuit(provider.id, next)
  ui.notify(`Circuit ${next === 'Open' ? 'opened' : 'closed'} for ${provider.name}.`)
}

async function invalidateCache(): Promise<void> {
  await ops.invalidateKeyCache()
  ui.notify('Key cache invalidated.')
}

onMounted(() => {
  void ops.loadHealth()
})
</script>

<template>
  <PageHeader title="Health & operations" description="Component status, usage recording and provider circuits.">
    <UiButton icon="refresh" :loading="ops.healthLoading" @click="ops.loadHealth">Refresh</UiButton>
  </PageHeader>

  <AsyncState :loading="ops.healthLoading" :error="ops.healthError" :empty="!health" empty-text="No health data yet." @retry="ops.loadHealth">
    <div v-if="health" class="flex flex-col gap-6">
      <UiCard title="Components" :meta="`Checked ${formatDateTime(health.checkedAt, true)}`" heading-id="ops-components">
        <ul class="m-0 list-none p-0">
          <li v-for="component in health.components" :key="component.name" class="flex flex-wrap items-center justify-between gap-3 border-b border-border py-2.5 last:border-b-0">
            <div class="min-w-0">
              <p class="font-medium">{{ component.name }}</p>
              <p v-if="component.description" class="text-small text-fg-2">{{ component.description }}</p>
            </div>
            <UiBadge :tone="HEALTH_STATUS[component.status].tone">{{ HEALTH_STATUS[component.status].label }}</UiBadge>
          </li>
        </ul>
        <dl class="mt-4 m-0">
          <DetailRow label="Admin API">{{ health.versions.adminApi }}</DetailRow>
          <DetailRow label="Gateway">{{ health.versions.gateway ?? 'Unavailable' }}</DetailRow>
          <DetailRow label="Schema">{{ health.versions.schema }}</DetailRow>
        </dl>
      </UiCard>

      <UiCard title="Usage writer" heading-id="ops-writer">
        <dl v-if="writer" class="m-0">
          <DetailRow label="Queue">{{ formatNumber(writer.queueDepth) }} of {{ formatNumber(writer.capacity) }}</DetailRow>
          <DetailRow label="In flight">{{ formatNumber(writer.inFlightRecords) }} records</DetailRow>
          <DetailRow label="Last write">{{ writer.lastWriteAt ? formatDateTime(writer.lastWriteAt, true) : 'No writes yet' }}</DetailRow>
          <DetailRow label="Consecutive failures">
            <UiBadge :tone="writer.consecutiveFailures > 0 ? 'danger' : 'ok'">{{ formatNumber(writer.consecutiveFailures) }}</UiBadge>
          </DetailRow>
        </dl>
        <p v-else class="text-fg-2">Usage writer status is unavailable.</p>
      </UiCard>

      <section aria-labelledby="ops-providers" class="flex flex-col gap-3">
        <h2 id="ops-providers" class="text-heading">Provider circuits</h2>
        <ProviderCircuitTable :providers="health.providers" @toggle="reviewCircuit" />
      </section>

      <UiCard title="Maintenance" heading-id="ops-maintenance">
        <div class="flex flex-wrap gap-3">
          <UiButton icon="sync" @click="cacheOpen = true">Invalidate key cache</UiButton>
          <UiButton icon="data_object" @click="configOpen = true">Export or import configuration</UiButton>
        </div>
        <p class="mt-3 text-small text-fg-3">Invalidating the key cache makes the gateway re-read every virtual key on its next request.</p>
      </UiCard>
    </div>
  </AsyncState>

  <ConfirmDialog
    v-model:open="circuitOpen"
    :title="isOpening ? `Open circuit for ${circuitTarget?.name ?? ''}?` : `Close circuit for ${circuitTarget?.name ?? ''}?`"
    :consequence="isOpening ? 'The gateway stops sending requests to this provider and uses fallbacks until the circuit is closed.' : 'The gateway resumes sending requests to this provider.'"
    :confirm-label="isOpening ? 'Open circuit' : 'Close circuit'"
    :danger="isOpening"
    :action="toggleCircuit"
  />
  <ConfirmDialog
    v-model:open="cacheOpen"
    title="Invalidate key cache?"
    consequence="Gateway instances re-read all virtual keys on their next request, which briefly increases database load."
    confirm-label="Invalidate cache"
    :action="invalidateCache"
  />
  <ConfigDialog v-model:open="configOpen" />
</template>
