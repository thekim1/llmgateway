<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { api } from '@/api'
import type { RequestLogSettings } from '@/api/types'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

const ui = useUiStore()

const settings = ref<RequestLogSettings | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)
const noTimeLimit = ref(false)
const confirmOpen = ref(false)
const confirmOffOpen = ref(false)

const durations = [
  { value: false, label: 'For 24 hours' },
  { value: true, label: 'No time limit' },
]

const status = computed(() => {
  const s = settings.value
  if (!s?.active) return { tone: 'neutral' as const, label: 'Off: only refused, failed and cancelled requests are logged' }
  return { tone: 'warn' as const, label: s.until ? `On until ${formatDateTime(s.until)}` : 'On, no time limit' }
})

const consequence = computed(() =>
  noTimeLimit.value
    ? 'The gateways will log every request until someone turns this off. Peak throughput drops by about 6–14 % and the log grows by one line per request.'
    : 'The gateways will log every request for the next 24 hours, then switch back by themselves. Peak throughput drops by about 6–14 % meanwhile and the log grows by one line per request.',
)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    settings.value = await api.settings.requestLog()
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

// A refusal from the server is shown in the confirmation dialog.
async function turnOn(): Promise<void> {
  settings.value = await api.settings.updateRequestLog(true, noTimeLimit.value)
  ui.notify('Logging every request')
}

async function turnOff(): Promise<void> {
  settings.value = await api.settings.updateRequestLog(false, false)
  ui.notify('Back to logging only problem requests')
}

onMounted(load)
</script>

<template>
  <UiCard title="Request logging" meta="Gateway log" heading-id="request-log-h" class="max-w-3xl">
    <AsyncState :loading="loading" :error="loadError" :empty="false" @retry="load">
      <div class="flex flex-col gap-3 text-fg-2">
        <div><UiBadge :tone="status.tone" :label="status.label" /></div>
        <p class="m-0">
          <strong class="text-fg">Every request is recorded either way.</strong> Each request made with a valid key is a usage record:
          time, key, team and department, model and provider, tokens, cost, latency, status and outcome, PII categories found and the routing
          rule that applied. Request counts, outcomes, latency and throughput are on
          <RouterLink :to="{ name: 'usage' }" class="text-accent-ink underline">Monitor › Usage</RouterLink>, and in the Data API and the
          <code class="font-mono text-small">ume.gateway.requests</code> metric. Requests refused before a key is recognised (missing,
          unknown or revoked keys) are counted separately as refused credentials. Message content is never logged or stored.
        </p>
        <p class="m-0">
          The gateway log normally has one line per refused, failed or cancelled request. Turn on logging every request to also see
          successful ones there, for example to follow traffic live in the container or log collector while you troubleshoot.
        </p>
      </div>

      <details class="mt-4 text-small text-fg-2">
        <summary class="cursor-pointer text-accent-ink">What it costs</summary>
        <ul class="mt-2 flex list-disc flex-col gap-1.5 pl-5">
          <li>Each request is formatted and written to the console and to OpenTelemetry. In our load tests peak throughput fell by about 6–14 % and the gateway’s CPU per request rose by about 10 %. At ordinary traffic the added time per request is a few microseconds.</li>
          <li>The log grows by one line per request: about 200 bytes in the console log (some 200 MB per million requests), more as an OpenTelemetry record, before your log system’s own overhead and retention.</li>
          <li>It applies to every gateway instance within seconds of saving, without a restart. The 24-hour option switches back by itself; with no time limit, remember to turn it off.</li>
          <li>Each line holds the request id, key prefix, model, provider, status, latency and fallbacks: nothing that is not in the usage record already.</li>
        </ul>
      </details>

      <div class="mt-4 flex flex-wrap items-center gap-3">
        <template v-if="settings?.active">
          <UiButton variant="primary" @click="confirmOffOpen = true">Turn off…</UiButton>
        </template>
        <template v-else>
          <SegmentedControl v-model="noTimeLimit" :options="durations" label="How long" mode="radio" />
          <UiButton variant="primary" @click="confirmOpen = true">Log every request…</UiButton>
        </template>
      </div>
      <p v-if="settings?.updatedAt" class="mb-0 mt-3 text-small text-fg-3">Last changed {{ formatDateTime(settings.updatedAt) }}. Changes are in the audit log.</p>
    </AsyncState>
  </UiCard>

  <ConfirmDialog v-model:open="confirmOpen" title="Log every request?" :consequence="consequence" confirm-label="Turn on" :action="turnOn" />
  <ConfirmDialog
    v-model:open="confirmOffOpen"
    title="Stop logging every request?"
    consequence="Successful requests will no longer be written to the gateway log. They stay in the usage records; refused, failed and cancelled requests are still logged."
    confirm-label="Turn off"
    :action="turnOff"
  />
</template>
