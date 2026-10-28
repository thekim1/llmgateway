<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { ModelPriceSync } from '@/api/types'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatNumber } from '@/utils/format'
import { problemMessage } from '@/utils/problem'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

const ui = useUiStore()
const status = ref<ModelPriceSync | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)
const runError = ref<unknown>(null)
const starting = ref(false)
let poll: ReturnType<typeof setTimeout> | undefined

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    status.value = await api.settings.modelPrices()
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

// A run starts in the background; check every few seconds until it has finished (at most two minutes).
function watchRun(since: string | null, tries = 40): void {
  clearTimeout(poll)
  if (tries === 0) return
  poll = setTimeout(async () => {
    try {
      status.value = await api.settings.modelPrices()
      if (!status.value.running && status.value.lastRunAt !== since) {
        ui.notify(status.value.error ? 'Price list check failed' : 'Model prices checked')
        return
      }
    } catch {
      // The next look at the page shows the state.
    }
    watchRun(since, tries - 1)
  }, 3000)
}

async function runNow(): Promise<void> {
  runError.value = null
  starting.value = true
  const since = status.value?.lastRunAt ?? null
  try {
    status.value = { ...(await api.settings.runModelPrices()), running: true }
    watchRun(since)
  } catch (e) {
    runError.value = e
  } finally {
    starting.value = false
  }
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(poll))
</script>

<template>
  <UiCard title="Model prices" meta="Checked daily" heading-id="model-prices-h" class="max-w-3xl">
    <AsyncState :loading="loading" :error="loadError" :empty="false" @retry="load">
      <div v-if="status" class="flex flex-col items-start gap-3 text-fg-2">
        <p class="m-0">
          Once a day the admin API reads the
          <a class="text-accent-ink underline" href="https://models.dev" target="_blank" rel="noopener noreferrer">models.dev<span class="sr-only"> (opens in a new tab)</span></a>
          list and adds a changed list price to the price history of every model that follows it, for Anthropic (also Claude on Foundry), Ollama
          Cloud, OpenAI and OpenRouter. It also fills in an empty description, capabilities and context window. Turn off <em>Price from
          models.dev</em> on a model or provider to set your own prices; models not in the list are priced by hand until they are. Azure
          deployments keep Azure’s list price from discovery and get the models.dev price only while they have none.
        </p>
        <template v-if="status.enabled">
          <p class="m-0">
            <template v-if="status.running">Checking now…</template>
            <template v-else-if="status.lastRunAt">
              Last checked {{ formatDateTime(status.lastRunAt) }}:
              <template v-if="status.error"><span class="text-danger">{{ status.error }}</span> Prices are unchanged.</template>
              <template v-else>
                {{ formatNumber(status.followed) }} models follow the list, {{ formatNumber(status.updated) }} prices updated,
                {{ formatNumber(status.enriched) }} models given details.
              </template>
            </template>
            <template v-else>Not checked yet.</template>
            <template v-if="status.nextRunAt && !status.running"> Next check {{ formatDateTime(status.nextRunAt) }}.</template>
          </p>
          <InlineError v-if="runError" :message="problemMessage(runError)" />
          <UiButton size="sm" icon="play_arrow" :loading="starting" :disabled="status.running" @click="runNow">Check now</UiButton>
          <p class="m-0 flex items-start gap-2 text-fg-3">
            <AppIcon name="info" :size="16" class="mt-px shrink-0" />
            <span>
              List prices are public prices; agreed discounts are not in them. Each change is in the audit log as
              <code class="font-mono text-small">system:price-list</code>. The status is that of the admin API instance that answered.
            </span>
          </p>
        </template>
        <p v-else class="m-0">
          Switched off: prices change only when you add them
          (<code class="font-mono text-small">Admin__AutoUpdatePrices</code>, <code class="font-mono text-small">Admin__PriceListUrl</code>).
        </p>
      </div>
    </AsyncState>
  </UiCard>
</template>
